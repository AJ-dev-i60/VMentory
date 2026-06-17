using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VMentory.Core;
using VMentory.Core.Persistence;
using VMentory.Web;

// ── Logging: errors only, no host/PII data ───────────────────────────────────
// Write to a writable location: VMENTORY_LOG, else the data dir (the mounted /data volume in the
// container — AppContext.BaseDirectory is the code dir and is NOT writable by the non-root container
// user). ErrorLogger is fail-safe: a bad/locked path degrades to no-op, never crashes startup.

using var logWriter = new ErrorLogger(ResolveLogPath());

// ── Apply any pending update before web infrastructure starts ─────────────────

Updater.ApplyPendingUpdate(logWriter);

// ── Configuration ────────────────────────────────────────────────────────────

var mockMode = args.Contains("--mock");
var dataDir = mockMode ? "" : ResolveDataDir();   // mock writes nothing to disk
var config = new AppConfig
{
    MockMode = mockMode,
    NoUpdate = args.Contains("--no-update"),
    VerboseMode = args.Contains("--verbose"),
    // Hosted service (ENG-0010): bind 0.0.0.0:{configurable port}, env-driven. The loopback +
    // random-port desktop bootstrap is gone. VMENTORY_HTTP_ONLY=1 disables TLS (reverse-proxy/dev).
    HttpAddr = EnvOr("VMENTORY_HTTP_ADDR", "0.0.0.0"),
    HttpOnly = EnvFlag("VMENTORY_HTTP_ONLY"),
    Port = ResolvePort(),
    // Interim auth (replaced by login + RBAC in slice 2). VMENTORY_TOKEN gives a stable token across
    // restarts for a hosted dev/prod instance; otherwise a fresh random token is generated each boot.
    Token = EnvOr("VMENTORY_TOKEN", GenerateToken()),
    WinRmPort = 5985,
    Persist = !mockMode,
    DataDir = dataDir,
    DbPath = ResolveDbPath(dataDir),
};

DevLog.Verbose = config.VerboseMode;

// ── Services ─────────────────────────────────────────────────────────────────

var store = new Store();
var hub = new EventHub();

var builder = WebApplication.CreateBuilder(args);

// ── Network bind + Core-terminated TLS (ENG-0010) ─────────────────────────────
// Bind the configured address/port directly via Kestrel and (unless VMENTORY_HTTP_ONLY) terminate
// HTTPS at Core itself. Cert is injected at runtime (operator PFX/PEM) with a self-signed fallback —
// nothing is baked into the image.
X509Certificate2? serverCert = null;
var tlsSource = "disabled (HTTP — VMENTORY_HTTP_ONLY)";
if (!config.HttpOnly)
    serverCert = TlsSetup.ResolveServerCertificate(config, out tlsSource);

builder.WebHost.ConfigureKestrel(k =>
{
    var ip = config.HttpAddr switch
    {
        "0.0.0.0" => IPAddress.Any,
        "::" => IPAddress.IPv6Any,
        _ => IPAddress.Parse(config.HttpAddr),
    };
    k.Listen(ip, config.Port, listen =>
    {
        if (serverCert != null) listen.UseHttps(serverCert);
    });
});

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(hub);
builder.Services.AddSingleton<IVirtualizationProvider, HyperVProvider>();
builder.Services.AddHostedService<Poller>();

// Persistence (slice 3): durable host registry + inventory snapshots. Off in mock mode — nothing
// touches disk there. The in-memory Store stays the working set; IInventoryStore is write-through.
if (config.Persist)
{
    builder.Services.AddDbContext<VMentoryDbContext>(o => o.UseSqlite($"Data Source={config.DbPath}"));
    builder.Services.AddScoped<IInventoryStore, EfInventoryStore>();
}
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

// ── Persistence: apply migrations + reload the registered hosts (non-mock) ─────
if (config.Persist)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
    db.Database.Migrate();

    var invStore = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
    foreach (var h in await invStore.LoadRegistryAsync())
        store.AddHost(h);
}

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

// Unauthenticated liveness probe (not under /api → not token-gated). For container/orchestrator
// health checks (Coolify, Docker HEALTHCHECK, k8s). Reveals no inventory data.
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = Updater.CurrentVersion }));

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

// Quit (graceful shutdown). Zeroes in-memory secrets/state; persisted registry + snapshots are KEPT
// (a service keeps its memory — the DB lives in a volume). No data purge.
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
app.MapPost("/api/hosts", async (HttpContext ctx, Store s, EventHub h, AppConfig cfg, IVirtualizationProvider provider, IServiceScopeFactory scopeFactory) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<AddHostsDto>();
    if (body == null || string.IsNullOrWhiteSpace(body.Addresses))
        return Results.BadRequest("addresses required");

    if (cfg.MockMode)
        return Results.Ok(new { added = 0, message = "Mock mode: use pre-loaded mock hosts" });

    var existingAddresses = s.GetAllHosts()
        .Select(h => h.Address.Trim().ToLowerInvariant())
        .ToHashSet();

    var addresses = body.Addresses
        .Split(['\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(a => !existingAddresses.Contains(a.Trim().ToLowerInvariant()))
        .Distinct()
        .ToList();

    // Add all hosts to the store immediately so they appear in the UI right away,
    // then run DNS / reachability / auth checks in the background.
    var hostsToCheck = new List<VMentory.Core.Host>();
    foreach (var addr in addresses)
    {
        var host = new VMentory.Core.Host
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

    // Persist the registry entries so the hosts survive restart (no creds — those aren't persisted).
    if (cfg.Persist && hostsToCheck.Count > 0)
    {
        using var scope = scopeFactory.CreateScope();
        var invStore = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
        foreach (var host in hostsToCheck)
            await invStore.UpsertRegistrationAsync(host);
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
                        (bool ok, string err) = await provider.QuickConnectAsync(storedHost);
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
app.MapDelete("/api/hosts/{id}", async (string id, Store s, EventHub h, AppConfig cfg, IServiceScopeFactory scopeFactory) =>
{
    if (!s.RemoveHost(id)) return Results.NotFound();

    if (cfg.Persist)
    {
        using var scope = scopeFactory.CreateScope();
        var invStore = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
        await invStore.RemoveAsync(id);   // cascades the host's snapshots
    }

    h.Broadcast("hostRemoved", new { hostId = id });
    return Results.Ok(new { ok = true });
});

// Trigger full scan (fire-and-forget — returns immediately while scans run in background)
app.MapPost("/api/scan", async (HttpContext ctx, Store s, EventHub h, AppConfig cfg, IVirtualizationProvider provider, IServiceScopeFactory scopeFactory) =>
{
    if (cfg.MockMode)
    {
        h.Broadcast("scanComplete", new { message = "Mock mode: data already loaded" });
        return Results.Ok(new { ok = true, message = "Mock mode" });
    }

    string? targetHostId = null;
    try
    {
        var body = await ctx.Request.ReadFromJsonAsync<ScanBody>();
        targetHostId = body?.HostId;
    }
    catch { }

    var hosts = s.GetAllHosts()
        .Where(h => h.Reachability.Auth == AuthState.Ok)
        .Where(h => targetHostId == null || h.Id == targetHostId)
        .ToList();

    if (hosts.Count == 0)
        return Results.Ok(new { ok = false, message = "No hosts with valid auth to scan" });

    // Previous inventory for the diff comes from the latest persisted snapshots (survives restart) —
    // "migrate diff logic onto snapshots" (ROADMAP 2.0). Loaded before this scan writes new ones.
    List<VMentory.Core.Host> previous;
    {
        using var scope = scopeFactory.CreateScope();
        var invStore = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
        previous = await invStore.GetLatestSnapshotHostsAsync(s.GetAllHosts().Select(hh => hh.Id));
    }

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

            var (ok, err) = await provider.ScanAsync(host);
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

            // Persist a snapshot of the freshly scanned inventory (the historical record + next diff base).
            if (ok)
            {
                var scanned = s.GetHost(host.Id);
                if (scanned != null)
                {
                    using var scope = scopeFactory.CreateScope();
                    var invStore = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
                    await invStore.SaveSnapshotAsync(scanned);
                }
            }

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
        s.RecordDiff(previous);
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

// ── Start + run ──────────────────────────────────────────────────────────────

var scheme = config.HttpOnly ? "http" : "https";
// A reachable host for the banner: localhost when bound to all interfaces, else the literal bind addr.
var displayHost = config.HttpAddr is "0.0.0.0" or "::" ? "localhost" : config.HttpAddr;
var url = $"{scheme}://{displayHost}:{config.Port}/?token={config.Token}";

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
Console.WriteLine($"\n  Bind  : {config.HttpAddr}:{config.Port}");
Console.WriteLine($"  TLS   : {tlsSource}");
if (!config.HttpOnly && tlsSource.StartsWith("self-signed"))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  [TLS]  — self-signed certificate; browsers will warn. Mount an operator cert");
    Console.WriteLine("           via VMENTORY_TLS_PFX or VMENTORY_TLS_CERT_PEM/VMENTORY_TLS_KEY_PEM.");
    Console.ResetColor();
}
Console.WriteLine($"\n  URL   : {url}");
Console.WriteLine($"  Token : {config.Token}");

await app.StartAsync();

Updater.StartBackgroundCheck(config, logWriter);

// Interactive dev convenience only: press Q to quit when attached to a real console. In a container
// (no TTY → stdin redirected) this is skipped; the operator stops the service with SIGTERM
// (`docker stop`), which ASP.NET Core handles as a graceful shutdown.
var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
if (!Console.IsInputRedirected)
{
    Console.WriteLine("\n  Press Q to quit\n");
    _ = Task.Run(async () =>
    {
        while (!appLifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (!Console.KeyAvailable) { await Task.Delay(200); continue; }
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.Q)
            {
                Console.WriteLine("\n  Shutting down...");
                appLifetime.StopApplication();
            }
        }
    });
}

await app.WaitForShutdownAsync();
store.ClearAll();   // zero in-memory secrets/state; persisted data is kept
Console.WriteLine("  Goodbye.");

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

static string GenerateToken()
{
    var bytes = RandomNumberGenerator.GetBytes(24);
    return Convert.ToBase64String(bytes)
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

static string EnvOr(string name, string fallback)
{
    var v = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(v) ? fallback : v;
}

static bool EnvFlag(string name)
{
    var v = Environment.GetEnvironmentVariable(name);
    return v is "1" or "true" or "TRUE" or "yes" or "on";
}

// Listen port: VMENTORY_HTTP_PORT, else a sensible default per scheme (8443 HTTPS / 8080 HTTP).
static int ResolvePort()
{
    var v = Environment.GetEnvironmentVariable("VMENTORY_HTTP_PORT");
    if (int.TryParse(v, out var p) && p is > 0 and < 65536) return p;
    return EnvFlag("VMENTORY_HTTP_ONLY") ? 8080 : 8443;
}

// Durable data directory (the container points this at a mounted volume via VMENTORY_DB). Holds the
// SQLite DB and the cached self-signed TLS cert. Created if missing. Empty in mock mode (no disk I/O).
static string ResolveDataDir()
{
    var fromEnv = Environment.GetEnvironmentVariable("VMENTORY_DB");
    var dir = !string.IsNullOrWhiteSpace(fromEnv)
        ? (Path.GetDirectoryName(Path.GetFullPath(fromEnv)) ?? "")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMentory");
    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    return dir;
}

// SQLite file path: VMENTORY_DB env var (the container points this at a mounted volume), else a
// per-user app-data file under the resolved data directory.
static string ResolveDbPath(string dataDir)
{
    var fromEnv = Environment.GetEnvironmentVariable("VMENTORY_DB");
    if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
    return Path.Combine(dataDir, "vmentory.db");
}

// errors.log path: VMENTORY_LOG, else the VMENTORY_DB directory (writable volume in the container),
// else the app base dir (fine for the Windows desktop exe). Never throws.
static string ResolveLogPath()
{
    try
    {
        var env = Environment.GetEnvironmentVariable("VMENTORY_LOG");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var db = Environment.GetEnvironmentVariable("VMENTORY_DB");
        var dir = !string.IsNullOrWhiteSpace(db)
            ? Path.GetDirectoryName(Path.GetFullPath(db))
            : AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = AppContext.BaseDirectory;
        return Path.Combine(dir, "errors.log");
    }
    catch { return Path.Combine(AppContext.BaseDirectory, "errors.log"); }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

record CredentialsDto(string Username, string Password);
record AddHostsDto(string Addresses, bool UseGlobalCreds = true, string? Username = null, string? Password = null);
record ScanBody(string? HostId);

// ── App config (registered as singleton) ─────────────────────────────────────

public class AppConfig
{
    public bool MockMode { get; init; }
    public bool NoUpdate { get; init; }
    public bool VerboseMode { get; init; }

    // Hosted runtime (ENG-0010). Bind 0.0.0.0:{Port} by default; Core terminates HTTPS unless HttpOnly.
    public string HttpAddr { get; init; } = "0.0.0.0";
    public int Port { get; init; }
    public bool HttpOnly { get; init; }

    public string Token { get; init; } = "";
    public int WinRmPort { get; set; } = 5985;

    // Persistence (slice 3). Off in mock mode (stays ephemeral). DataDir holds the SQLite DB + cached
    // self-signed TLS cert (the container points VMENTORY_DB at a mounted volume). DbPath is the DB file.
    public bool Persist { get; init; }
    public string DataDir { get; init; } = "";
    public string DbPath { get; init; } = "";
}

// ── Error logger (errors only, no PII) ────────────────────────────────────────

public class ErrorLogger : IDisposable
{
    private readonly StreamWriter? _writer;   // null → logging disabled (never fatal)
    private readonly object _lock = new();

    public ErrorLogger(string path)
    {
        _writer = TryOpen(path) ?? TryOpen(Path.Combine(Path.GetTempPath(), "vmentory-errors.log"));
        if (_writer == null)
            Console.Error.WriteLine($"  [warn] error log unavailable at '{path}' — continuing without a log file.");
    }

    private static StreamWriter? TryOpen(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            return new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch { return null; }
    }

    public void LogError(string message, Exception? ex = null)
    {
        if (_writer == null) return;
        lock (_lock)
        {
            try
            {
                _writer.WriteLine($"[{DateTimeOffset.UtcNow:o}] ERROR: {message}");
                if (ex != null)
                    _writer.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            }
            catch { /* logging must never throw */ }
        }
    }

    public void Dispose() => _writer?.Dispose();
}
