using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperInventory;

// ── Logging: errors only, no host/PII data ───────────────────────────────────

var logPath = Path.Combine(AppContext.BaseDirectory, "errors.log");
using var logWriter = new ErrorLogger(logPath);

// ── Apply any pending update before web infrastructure starts ─────────────────

Updater.ApplyPendingUpdate(logWriter);

// ── Configuration ────────────────────────────────────────────────────────────

var config = new AppConfig
{
    MockMode = args.Contains("--mock"),
    NoUpdate = args.Contains("--no-update"),
    VerboseMode = args.Contains("--verbose"),
    Port = FindFreePort(),
    Token = GenerateToken(),
    WinRmPort = 5985,
};

DevLog.Verbose = config.VerboseMode;

// ── Services ─────────────────────────────────────────────────────────────────

var store = new Store();
var hub = new EventHub();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{config.Port}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(hub);
builder.Services.AddHostedService<Poller>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft", LogLevel.None);
builder.Logging.AddFilter("System", LogLevel.None);

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));

// ── Load mock data ────────────────────────────────────────────────────────────

if (config.MockMode)
{
    foreach (var h in MockData.Generate())
        store.AddHost(h);
}

// ── Build app ────────────────────────────────────────────────────────────────

var app = builder.Build();

// All responses: no caching
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store, no-cache";
    ctx.Response.Headers.Pragma = "no-cache";
    await next();
});

// ── Serve index.html from embedded resource ───────────────────────────────────

var indexHtml = LoadEmbeddedHtml();
app.MapGet("/", () => Results.Bytes(indexHtml, "text/html; charset=utf-8"));
app.MapGet("/index.html", () => Results.Bytes(indexHtml, "text/html; charset=utf-8"));

// ── Token middleware (except /health) ────────────────────────────────────────

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var tok = ctx.Request.Headers["X-Session-Token"].FirstOrDefault()
                  ?? ctx.Request.Query["token"].FirstOrDefault();
        if (tok != config.Token)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Unauthorized");
            return;
        }
    }
    await next();
});

// ── API Routes ────────────────────────────────────────────────────────────────

// State + totals
app.MapGet("/api/state", (Store s, AppConfig cfg) => Results.Ok(new
{
    hosts = s.GetAllHosts(),
    totals = s.ComputeTotals(),
    diff = s.GetDiff(),
    credentialsSet = s.HasGlobalCreds,
    mockMode = cfg.MockMode,
}));

// Quit (purge + shutdown)
app.MapPost("/api/quit", (Store s, IHostApplicationLifetime life) =>
{
    s.ClearAll();
    life.StopApplication();
    return Results.Ok(new { ok = true });
});

// Set global credentials
app.MapPost("/api/credentials", async (HttpContext ctx, Store s, EventHub h) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<CredentialsDto>();
    if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
        return Results.BadRequest("username and password are required");

    s.SetGlobalCredentials(body.Username, body.Password);
    h.Broadcast("credentialsSet", new { ok = true });
    return Results.Ok(new { ok = true });
});

// Add host(s)
app.MapPost("/api/hosts", async (HttpContext ctx, Store s, EventHub h, AppConfig cfg) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<AddHostsDto>();
    if (body == null || string.IsNullOrWhiteSpace(body.Addresses))
        return Results.BadRequest("addresses required");

    if (cfg.MockMode)
        return Results.Ok(new { added = 0, message = "Mock mode: use pre-loaded mock hosts" });

    var addresses = body.Addresses
        .Split(['\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct()
        .ToList();

    // Add all hosts to the store immediately so they appear in the UI right away,
    // then run DNS / reachability / auth checks in the background.
    var hostsToCheck = new List<HyperInventory.Host>();
    foreach (var addr in addresses)
    {
        var host = new HyperInventory.Host
        {
            Address = addr,
            Fqdn = addr,
            UseGlobalCreds = body.UseGlobalCreds,
            Connecting = true,
        };
        if (!body.UseGlobalCreds && !string.IsNullOrWhiteSpace(body.Username))
            host.PerHostCreds = new Credentials(body.Username!, body.Password ?? "");

        s.AddHost(host);
        h.Broadcast("hostAdded", host);
        hostsToCheck.Add(host);
    }

    _ = Task.Run(async () =>
    {
        foreach (var host in hostsToCheck)
        {
            var addr = host.Address;
            try
            {
                // DNS
                DevLog.Step($"[ADD]  resolving DNS for {addr}");
                var (resolved, fqdn, dnsErr) = await ReachabilityChecker.ResolveFqdnAsync(addr);
                s.UpdateHost(host.Id, hh => hh.Fqdn = fqdn);

                if (!resolved)
                {
                    DevLog.Err($"[ADD]  DNS failed for {addr}: {dnsErr}");
                    s.UpdateHost(host.Id, hh =>
                    {
                        hh.AddError = $"DNS resolution failed: {dnsErr}";
                        hh.Reachability.CheckedAt = DateTimeOffset.UtcNow;
                        hh.Connecting = false;
                    });
                    var gone = s.GetHost(host.Id);
                    if (gone != null) h.Broadcast("hostUpdated", gone);
                    continue;
                }
                DevLog.Ok($"[ADD]  DNS OK → {fqdn}");

                // Reachability
                var icmp = await ReachabilityChecker.PingAsync(addr, TimeSpan.FromSeconds(2));
                var winrm = await ReachabilityChecker.TestTcpPortAsync(addr, cfg.WinRmPort, TimeSpan.FromSeconds(3));
                s.UpdateHost(host.Id, hh =>
                {
                    hh.Reachability.CheckedAt = DateTimeOffset.UtcNow;
                    hh.Reachability.Icmp = icmp;
                    hh.Reachability.WinRm = winrm;
                });
                var afterReach = s.GetHost(host.Id);
                if (afterReach != null)
                    h.Broadcast("reachability", new { hostId = host.Id, reachability = afterReach.Reachability });

                if (!winrm)
                {
                    s.UpdateHost(host.Id, hh =>
                    {
                        hh.Reachability.Auth = AuthState.Unknown;
                        hh.AddError = !icmp ? "Host unreachable (ICMP failed)" : "WinRM port not responding";
                        hh.Connecting = false;
                    });
                    DevLog.Warn($"[ADD]  {addr} — ICMP={icmp}, WinRM={winrm}");
                    var afterWinrm = s.GetHost(host.Id);
                    if (afterWinrm != null) h.Broadcast("hostUpdated", afterWinrm);
                    continue;
                }

                // Auth
                var creds = s.GetEffectiveCreds(host);
                if (creds == null)
                {
                    DevLog.Warn($"[ADD]  no credentials available for {addr}");
                    s.UpdateHost(host.Id, hh =>
                    {
                        hh.Reachability.Auth = AuthState.Unknown;
                        hh.AddError = "No credentials configured — set global credentials first";
                        hh.Connecting = false;
                    });
                    var afterNoCreds = s.GetHost(host.Id);
                    if (afterNoCreds != null) h.Broadcast("hostUpdated", afterNoCreds);
                    continue;
                }

                DevLog.Step($"[ADD]  using creds: username='{creds.Username}', useGlobal={host.UseGlobalCreds}");
                await ReachabilityChecker.EnsureTrustedHostAsync(addr);
                var (authState, authErr) = await ReachabilityChecker.TestWinRmAuthAsync(
                    addr, creds, cfg.WinRmPort, TimeSpan.FromSeconds(30));
                s.UpdateHost(host.Id, hh =>
                {
                    hh.Reachability.Auth = authState;
                    hh.Reachability.ErrorDetail = authErr;
                });

                if (authState == AuthState.Ok)
                {
                    // QuickConnectAsync modifies the host object in place (Fqdn, OsCaption, Model, etc.)
                    var storedHost = s.GetHost(host.Id);
                    if (storedHost != null)
                    {
                        (bool ok, string err) = await Scanner.QuickConnectAsync(storedHost, creds, cfg.WinRmPort);
                        s.UpdateHost(host.Id, hh => { if (!ok) hh.AddError = err; hh.Connecting = false; });
                    }
                }
                else
                {
                    DevLog.Err($"[ADD]  auth failed for {addr}: {authErr}");
                    s.UpdateHost(host.Id, hh =>
                    {
                        hh.AddError = $"Authentication failed: {authErr}";
                        hh.Connecting = false;
                    });
                }

                var final = s.GetHost(host.Id);
                if (final != null) h.Broadcast("hostUpdated", final);
            }
            catch (Exception ex)
            {
                logWriter.LogError($"Add host failed for {addr}", ex);
                s.UpdateHost(host.Id, hh => { hh.AddError = "Unexpected error during connect"; hh.Connecting = false; });
                var afterErr = s.GetHost(host.Id);
                if (afterErr != null) h.Broadcast("hostUpdated", afterErr);
            }
        }
    });

    return Results.Ok(new { added = addresses.Count });
});

// Remove host
app.MapDelete("/api/hosts/{id}", (string id, Store s, EventHub h) =>
{
    if (!s.RemoveHost(id)) return Results.NotFound();
    h.Broadcast("hostRemoved", new { hostId = id });
    return Results.Ok(new { ok = true });
});

// Trigger full scan (fire-and-forget — returns immediately while scans run in background)
app.MapPost("/api/scan", (Store s, EventHub h, AppConfig cfg) =>
{
    if (cfg.MockMode)
    {
        h.Broadcast("scanComplete", new { message = "Mock mode: data already loaded" });
        return Results.Ok(new { ok = true, message = "Mock mode" });
    }

    var hosts = s.GetAllHosts()
        .Where(h => h.Reachability.Auth == AuthState.Ok)
        .ToList();

    if (hosts.Count == 0)
        return Results.Ok(new { ok = false, message = "No hosts with valid auth to scan" });

    // Snapshot VMs before scan for diff computation
    var snapshot = s.GetAllHosts().Select(h => new HyperInventory.Host
    {
        Id = h.Id,
        Vms = [.. h.Vms.Select(v => new Vm
        {
            Name = v.Name, HostId = v.HostId, VCpuCount = v.VCpuCount, AssignedRamMb = v.AssignedRamMb
        })]
    }).ToList();

    // Run scans with max 3 concurrent
    var sem = new SemaphoreSlim(3, 3);
    var tasks = hosts.Select(async host =>
    {
        await sem.WaitAsync();
        try
        {
            s.UpdateHost(host.Id, h => { h.ScanState = ScanState.Scanning; h.ScanError = ""; });
            h.Broadcast("scanProgress", new { hostId = host.Id, state = "scanning" });

            var creds = s.GetEffectiveCreds(host);
            if (creds == null)
            {
                s.UpdateHost(host.Id, h => { h.ScanState = ScanState.Error; h.ScanError = "No credentials"; });
                h.Broadcast("scanProgress", new { hostId = host.Id, state = "error", error = "No credentials" });
                return;
            }

            var (ok, err) = await Scanner.ScanHostAsync(host, creds, cfg.WinRmPort);
            var now = DateTimeOffset.UtcNow;

            s.UpdateHost(host.Id, hh =>
            {
                if (ok)
                {
                    hh.ScanState = ScanState.Done;
                    hh.LastScanned = now;
                    hh.ScanError = "";
                    // copy scanned data into store
                    hh.Fqdn = host.Fqdn;
                    hh.OsCaption = host.OsCaption;
                    hh.OsVersion = host.OsVersion;
                    hh.LastBoot = host.LastBoot;
                    hh.Manufacturer = host.Manufacturer;
                    hh.Model = host.Model;
                    hh.Serial = host.Serial;
                    hh.CpuModel = host.CpuModel;
                    hh.SocketCount = host.SocketCount;
                    hh.TotalCores = host.TotalCores;
                    hh.TotalLogicalProcs = host.TotalLogicalProcs;
                    hh.TotalRamGb = host.TotalRamGb;
                    hh.Volumes = host.Volumes;
                    hh.Vms = host.Vms;
                }
                else
                {
                    hh.ScanState = ScanState.Error;
                    hh.ScanError = err;
                }
            });

            h.Broadcast("scanProgress", new
            {
                hostId = host.Id,
                state = ok ? "done" : "error",
                error = ok ? null : err,
                host = s.GetHost(host.Id)
            });
        }
        catch (Exception ex)
        {
            logWriter.LogError($"Scan failed for host {host.Id}", ex);
            s.UpdateHost(host.Id, hh => { hh.ScanState = ScanState.Error; hh.ScanError = "Unexpected error"; });
            h.Broadcast("scanProgress", new { hostId = host.Id, state = "error", error = "Unexpected error" });
        }
        finally
        {
            sem.Release();
        }
    });

    _ = Task.WhenAll(tasks).ContinueWith(_ =>
    {
        s.RecordDiff(snapshot);
        h.Broadcast("scanComplete", new { totals = s.ComputeTotals(), diff = s.GetDiff() });
    });

    return Results.Ok(new { ok = true, scanning = hosts.Count });
});

// SSE event stream
app.MapGet("/api/events", async (HttpContext ctx, IHostApplicationLifetime lifetime) =>
{
    var tok = ctx.Request.Headers["X-Session-Token"].FirstOrDefault()
              ?? ctx.Request.Query["token"].FirstOrDefault();
    if (tok != config.Token) { ctx.Response.StatusCode = 401; return; }

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
        ctx.RequestAborted, lifetime.ApplicationStopping);

    var clientId = hub.Subscribe();
    await hub.StreamAsync(clientId, ctx.Response, cts.Token);
});

// Export JSON
app.MapGet("/api/export/json", (Store s) =>
{
    var data = Exporter.ToJson(s.GetAllHosts(), s.ComputeTotals(), s.GetDiff());
    return Results.File(data, "application/json",
        $"VMentory-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
});

// Export CSV zip
app.MapGet("/api/export/csv", (Store s) =>
{
    var data = Exporter.ToCsvZip(s.GetAllHosts());
    return Results.File(data, "application/zip",
        $"VMentory-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
});

// ── Start + console loop ─────────────────────────────────────────────────────

var url = $"http://127.0.0.1:{config.Port}/?token={config.Token}";

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine(@"
  ╔══════════════════════════════════════════════╗
  ║                  VMentory                    ║
  ╚══════════════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine($"  Version : v{Updater.CurrentVersion}");
if (config.MockMode)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  [MOCK MODE] — using simulated data, no real WinRM calls");
    Console.ResetColor();
}
if (config.VerboseMode)
{
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine("  [VERBOSE] — full diagnostic logging enabled");
    Console.ResetColor();
}
Console.WriteLine($"\n  URL   : {url}");
Console.WriteLine($"  Token : {config.Token}");
Console.WriteLine("\n  Press Q to quit and purge data");
Console.WriteLine("  Press R to re-open browser\n");

await app.StartAsync();

// Ensure local WinRM service is running so WSMan:\ provider and TrustedHosts work.
if (!config.MockMode)
    await ReachabilityChecker.EnsureWinRmServiceAsync();

Updater.StartBackgroundCheck(config, logWriter);

OpenBrowser(url);

// Console input loop (runs until Q)
var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
_ = Task.Run(async () =>
{
    while (!appLifetime.ApplicationStopping.IsCancellationRequested)
    {
        if (!Console.KeyAvailable) { await Task.Delay(200); continue; }
        var key = Console.ReadKey(intercept: true).Key;
        if (key == ConsoleKey.Q)
        {
            Console.WriteLine("\n  Purging session data and shutting down...");
            store.ClearAll();
            appLifetime.StopApplication();
        }
        else if (key == ConsoleKey.R)
        {
            Console.WriteLine("  Re-opening browser...");
            OpenBrowser(url);
        }
    }
});

await app.WaitForShutdownAsync();
store.ClearAll();
Console.WriteLine("  Session data purged. Goodbye.");

// ── Helpers ───────────────────────────────────────────────────────────────────

static byte[] LoadEmbeddedHtml()
{
    var asm = System.Reflection.Assembly.GetEntryAssembly()!;
    var name = asm.GetManifestResourceNames()
        .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("Embedded index.html not found. Build resources are missing.");
    using var stream = asm.GetManifestResourceStream(name)!;
    var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
}

static int FindFreePort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static string GenerateToken()
{
    var bytes = RandomNumberGenerator.GetBytes(24);
    return Convert.ToBase64String(bytes)
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

static void OpenBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { Console.WriteLine($"  Could not open browser automatically. Navigate to: {url}"); }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

record CredentialsDto(string Username, string Password);
record AddHostsDto(string Addresses, bool UseGlobalCreds = true, string? Username = null, string? Password = null);

// ── App config (registered as singleton) ─────────────────────────────────────

public class AppConfig
{
    public bool MockMode { get; init; }
    public bool NoUpdate { get; init; }
    public bool VerboseMode { get; init; }
    public int Port { get; init; }
    public string Token { get; init; } = "";
    public int WinRmPort { get; set; } = 5985;
}

// ── Error logger (errors only, no PII) ────────────────────────────────────────

public class ErrorLogger(string path) : IDisposable
{
    private readonly StreamWriter _writer = new(
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    private readonly object _lock = new();

    public void LogError(string message, Exception? ex = null)
    {
        lock (_lock)
        {
            _writer.WriteLine($"[{DateTimeOffset.UtcNow:o}] ERROR: {message}");
            if (ex != null)
                _writer.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose() => _writer.Dispose();
}
