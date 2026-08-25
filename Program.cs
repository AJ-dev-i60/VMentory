using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using VMentory.Core;
using VMentory.Core.Auth;
using VMentory.Core.Persistence;
using VMentory.Core.Secrets;
using VMentory.Web;

// ── Logging: errors only, no host/PII data ───────────────────────────────────
// Write to a writable location: VMENTORY_LOG, else the data dir (the mounted /data volume in the
// container — AppContext.BaseDirectory is the code dir and is NOT writable by the non-root container
// user). ErrorLogger is fail-safe: a bad/locked path degrades to no-op, never crashes startup.

using var logWriter = new ErrorLogger(ResolveLogPath());

// ── Configuration ────────────────────────────────────────────────────────────

var mockMode = args.Contains("--mock");
var dataDir = mockMode ? "" : ResolveDataDir();   // mock writes nothing to disk
var config = new AppConfig
{
    MockMode    = mockMode,
    NoUpdate    = args.Contains("--no-update"),  // kept for CLI compat — Updater.cs removed
    BuildStamp  = ComputeBuildStamp(),
    VerboseMode = args.Contains("--verbose"),
    // Hosted service (ENG-0010): bind 0.0.0.0:{configurable port}, env-driven.
    HttpAddr = EnvOr("VMENTORY_HTTP_ADDR", "0.0.0.0"),
    HttpOnly = EnvFlag("VMENTORY_HTTP_ONLY"),
    Port     = ResolvePort(),
    WinRmPort = 5985,
    Persist   = !mockMode,
    DataDir   = dataDir,
    DbPath    = ResolveDbPath(dataDir),
};

DevLog.Verbose = config.VerboseMode;

// ── KEK — secret store root key (ENG-0002) ───────────────────────────────────
// VMENTORY_KEK: base64(32 bytes). If set in real mode, credentials and future secrets are
// encrypted with AES-256-GCM and persisted across restarts. If not set, an in-memory
// EphemeralSecretStore is used — credentials survive the process but are lost on restart.
byte[]? kek = null;
if (config.Persist)
{
    var kekEnv = Environment.GetEnvironmentVariable("VMENTORY_KEK");
    if (!string.IsNullOrWhiteSpace(kekEnv))
    {
        try
        {
            var decoded = Convert.FromBase64String(kekEnv);
            if (decoded.Length == 32) kek = decoded;
            else Console.Error.WriteLine("  [WARN] VMENTORY_KEK must be exactly 32 bytes (base64). Falling back to ephemeral secret store.");
        }
        catch { Console.Error.WriteLine("  [WARN] VMENTORY_KEK is not valid base64. Falling back to ephemeral secret store."); }
    }
}

// ── Services ─────────────────────────────────────────────────────────────────

var store = new Store();
var hub   = new EventHub();

var builder = WebApplication.CreateBuilder(args);

// ── Network bind + Core-terminated TLS (ENG-0010) ─────────────────────────────
X509Certificate2? serverCert = null;
var tlsSource = "disabled (HTTP — VMENTORY_HTTP_ONLY)";
if (!config.HttpOnly)
    serverCert = TlsSetup.ResolveServerCertificate(config, out tlsSource);

builder.WebHost.ConfigureKestrel(k =>
{
    var ip = config.HttpAddr switch
    {
        "0.0.0.0" => IPAddress.Any,
        "::"      => IPAddress.IPv6Any,
        _         => IPAddress.Parse(config.HttpAddr),
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
builder.Services.AddSingleton<IVirtualizationProvider, ProxmoxProvider>();
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddHostedService<Poller>();

// ── Estate dashboard + remediation tracker (ENG-0015) ─────────────────────────
// The hardware monitor (Dell OpenManage) is optional config: VMENTORY_OME_URL/USER/PASSWORD
// (+ VMENTORY_OME_SKIP_TLS=1 for its self-signed cert, VMENTORY_OME_INTERVAL seconds). Without it
// the estate view still renders — machines, inventory and the action list — minus hardware health.
var omeOptions = OmeOptions.FromEnvironment();
builder.Services.AddSingleton(omeOptions);
builder.Services.AddSingleton(new EstateState { MonitorConfigured = omeOptions.Enabled });
builder.Services.AddSingleton<EstateCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EstateCollector>());

// ── Auth: cookie-based session (ENG-0008, slice 2) + SSO (ENG-0014) ───────────
// HttpOnly + Secure (when HTTPS). EventSource (GET, same-origin) sends cookies automatically — no
// more ?token= on the SSE stream. CSRF is blocked by SameSite (Strict, or Lax once OIDC is on —
// see the note at the Cookie.SameSite assignment below).
// Lands the "OIDC seam later" half of ENG-0008. The issuer is configuration, never a hardcoded
// provider, so one build serves Pocket-ID on the dev instance and any other OP on a customer
// deployment. Absent config, none of this registers and the app behaves exactly as before.
var oidcIssuer   = Environment.GetEnvironmentVariable("VMENTORY_OIDC_ISSUER")?.TrimEnd('/');
var oidcClientId = Environment.GetEnvironmentVariable("VMENTORY_OIDC_CLIENT_ID");
var oidcSecret   = Environment.GetEnvironmentVariable("VMENTORY_OIDC_CLIENT_SECRET");
var oidcEnabled  = !string.IsNullOrWhiteSpace(oidcIssuer)
                && !string.IsNullOrWhiteSpace(oidcClientId)
                && !string.IsNullOrWhiteSpace(oidcSecret);
var oidcName     = EnvOr("VMENTORY_OIDC_NAME", "SSO");
var oidcAllowed  = (Environment.GetEnvironmentVariable("VMENTORY_OIDC_ALLOWED_EMAILS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(e => e.ToLowerInvariant())
    .ToHashSet();
var oidcRole = Enum.TryParse<AppRole>(EnvOr("VMENTORY_OIDC_ROLE", "Admin"), ignoreCase: true, out var parsedOidcRole)
    ? parsedOidcRole
    : AppRole.Admin;
// Password sign-in stays on by default. VMENTORY_PASSWORD_LOGIN=0 makes SSO the only door — and
// is the break-glass in reverse: set it back to 1 and restart to get the local login returned.
var passwordLoginEnabled = Environment.GetEnvironmentVariable("VMENTORY_PASSWORD_LOGIN") != "0";

var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name     = "vmentory_session";
        o.Cookie.HttpOnly = true;
        // SameSite: Strict blocks CSRF outright, but it also withholds the cookie on the first
        // request after an external redirect — which is exactly what an SSO callback is, so a
        // Strict cookie can leave a user looking signed-out the instant they finish signing in.
        // With OIDC on we drop to Lax: the cookie still never rides a cross-site POST/PATCH/DELETE,
        // so every state-changing verb keeps its CSRF protection. Only top-level GET navigation
        // carries the session, and a cross-origin page cannot read those responses anyway.
        o.Cookie.SameSite = oidcEnabled ? SameSiteMode.Lax : SameSiteMode.Strict;
        o.Cookie.SecurePolicy = config.HttpOnly
            ? CookieSecurePolicy.None
            : CookieSecurePolicy.Always;
        o.ExpireTimeSpan  = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        // Return 401 JSON instead of redirecting to a login page (this is an API + SPA).
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
    });

if (oidcEnabled)
{
    authBuilder.AddOpenIdConnect(o =>
    {
        o.Authority    = oidcIssuer;
        o.ClientId     = oidcClientId!;   // guarded: oidcEnabled required all three to be non-blank
        o.ClientSecret = oidcSecret!;
        o.ResponseType = "code";
        o.UsePkce      = true;   // Pocket-ID clients are created with pkceEnabled, which *requires* PKCE
        o.CallbackPath = "/api/auth/oidc/callback";
        o.SaveTokens   = false;  // nothing downstream needs the access token
        o.GetClaimsFromUserInfoEndpoint = true;
        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("email");
        o.Scope.Add("profile");

        // The provider's principal is discarded and rebuilt. RbacCatalog reads ClaimTypes.Role, so
        // an SSO session must carry exactly the same claim shape the password path issues or every
        // authorization check silently fails closed.
        o.Events.OnTokenValidated = async ctx =>
        {
            var email = (ctx.Principal?.FindFirstValue(ClaimTypes.Email)
                      ?? ctx.Principal?.FindFirstValue("email")
                      ?? "").Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(email))
            {
                ctx.Fail("The identity provider returned no email claim.");
                return;
            }

            if (oidcAllowed.Count > 0 && !oidcAllowed.Contains(email))
            {
                await RecordOidcAudit(ctx.HttpContext, email, allowed: false);
                ctx.Fail("This account is not allowed to sign in to VMentory.");
                return;
            }

            var role = oidcRole;
            if (config.Persist)
            {
                var scopeFactory = ctx.HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
                using var scope  = scopeFactory.CreateScope();
                var userStore    = scope.ServiceProvider.GetRequiredService<IUserStore>();

                var user = await userStore.FindByUsernameAsync(email);
                if (user == null)
                {
                    user = new AppUserEntity
                    {
                        Username = email,
                        // Deliberately not a hash. PasswordHasher.Verify requires
                        // iterations:salt:hash and returns false for anything else, so this
                        // account can never be reached through /api/auth/login.
                        PasswordHash       = "",
                        Role               = oidcRole,
                        MustChangePassword = false,
                        CreatedAt          = DateTimeOffset.UtcNow,
                    };
                    await userStore.CreateAsync(user);
                }

                // An operator may have changed the role by hand since first sign-in; respect the
                // stored value instead of re-stamping VMENTORY_OIDC_ROLE on every login.
                role = user.Role;
                user.LastLoginAt = DateTimeOffset.UtcNow;
                await userStore.UpdateAsync(user);
                await RecordOidcAudit(ctx.HttpContext, email, allowed: true);
            }

            // SSO accounts are never placed in forced rotation — they have no password to rotate,
            // and the chokepoint would pin them to /api/auth/* forever.
            var claims = BuildClaims(email, role, mustChangePassword: false);
            claims.Add(new Claim("auth_mode", "oidc"));
            ctx.Principal = new ClaimsPrincipal(
                new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        };

        // Without these a failed sign-in surfaces as an unhandled exception page rather than
        // something the SPA can show the operator.
        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/?sso_error=" + Uri.EscapeDataString(ctx.Failure?.Message ?? "Sign-in failed"));
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
        o.Events.OnAccessDenied = ctx =>
        {
            ctx.Response.Redirect("/?sso_error=" + Uri.EscapeDataString("Access denied"));
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    });
}

builder.Services.AddAuthorization();

// ── Persistence (slice 3) ─────────────────────────────────────────────────────
if (config.Persist)
{
    builder.Services.AddDbContext<VMentoryDbContext>(o => o.UseSqlite($"Data Source={config.DbPath}"));
    builder.Services.AddScoped<IInventoryStore, EfInventoryStore>();
    builder.Services.AddScoped<IUserStore, EfUserStore>();
}

// ── Secret store (ENG-0002, slice 3) ─────────────────────────────────────────
// AesGcmSecretStore (DB-backed, Scoped) when a valid 32-byte KEK is provided in real mode;
// EphemeralSecretStore (in-memory, Singleton) otherwise. Call sites inject ISecretStore — they
// never know which impl is running.
if (kek != null && config.Persist)
{
    builder.Services.AddSingleton(new DekProvider(kek));
    builder.Services.AddScoped<ISecretStore, AesGcmSecretStore>();
}
else
{
    builder.Services.AddSingleton<ISecretStore, EphemeralSecretStore>();
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

if (config.MockMode)
{
    foreach (var h in MockData.Generate())
        store.AddHost(h);
}

var app = builder.Build();

// ── Persistence: apply migrations + seed ──────────────────────────────────────
if (config.Persist)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
    db.Database.Migrate();

    var invStore   = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
    var userStore  = scope.ServiceProvider.GetRequiredService<IUserStore>();
    var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();

    foreach (var h in await invStore.LoadRegistryAsync())
        store.AddHost(h);

    await SeedAdminUserAsync(userStore);
    await LoadPersistedCredsAsync(store, secretStore);

    // ENG-0015: first-run estate import (empty tables only) + last hardware reading into memory.
    var estateState = app.Services.GetRequiredService<EstateState>();
    await EstateSeeder.SeedAsync(db, estateState);
    await estateState.LoadLatestAsync(db);
}

// ── Middleware pipeline ────────────────────────────────────────────────────────

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store, no-cache";
    ctx.Response.Headers.Pragma       = "no-cache";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// ── Serve index.html ──────────────────────────────────────────────────────────

var indexHtml = LoadEmbeddedHtml();
app.MapGet("/", () => Results.Bytes(indexHtml, "text/html; charset=utf-8"));
app.MapGet("/index.html", () => Results.Bytes(indexHtml, "text/html; charset=utf-8"));

// Unauthenticated liveness probe.
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = AppVersion(), build = config.BuildStamp }));

// ── Auth chokepoint middleware (ENG-0008) ─────────────────────────────────────
// Single enforcement point: gates all /api/* routes. Authenticated users with MustChangePassword
// are locked to /api/auth/* only until they rotate their password.
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(); return; }

    // /api/auth/* is always open (login, logout, change-password, me).
    if (ctx.Request.Path.StartsWithSegments("/api/auth")) { await next(); return; }

    if (!ctx.User.Identity?.IsAuthenticated ?? true)
    {
        await WriteAuditIfPossible(ctx, "api_access", allowed: false);
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "Not authenticated" });
        return;
    }

    // Enforce password-rotation gate: until changed, only /api/auth/* is reachable.
    var mustChange = ctx.User.FindFirstValue("must_change_password") == "true";
    if (mustChange)
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = "Password change required", requiresPasswordChange = true });
        return;
    }

    await next();
});

// ── /api/auth routes ──────────────────────────────────────────────────────────

// POST /api/auth/login
app.MapPost("/api/auth/login", async (HttpContext ctx, IServiceScopeFactory scopeFactory) =>
{
    if (!passwordLoginEnabled)
        return Results.Json(new { error = "Password sign-in is disabled on this deployment" }, statusCode: 403);

    var body = await ctx.Request.ReadFromJsonAsync<LoginDto>();
    if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
        return Results.BadRequest(new { error = "username and password required" });

    if (!config.Persist)
    {
        // Mock/dev mode: accept any credentials as Admin (no DB in mock mode).
        var mockClaims = BuildClaims(body.Username, AppRole.Admin, mustChangePassword: false);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(mockClaims, CookieAuthenticationDefaults.AuthenticationScheme)));
        return Results.Ok(new { role = AppRole.Admin.ToString(), mustChangePassword = false, mockMode = true });
    }

    using var scope    = scopeFactory.CreateScope();
    var userStore      = scope.ServiceProvider.GetRequiredService<IUserStore>();
    var user           = await userStore.FindByUsernameAsync(body.Username.Trim());

    var allowed = user != null && PasswordHasher.Verify(body.Password, user.PasswordHash);
    await userStore.WriteAuditAsync(new AuditEventEntity
    {
        Timestamp = DateTimeOffset.UtcNow,
        Username  = body.Username.Trim(),
        Verb      = "login",
        Allowed   = allowed,
        CorrelationId = ctx.TraceIdentifier,
    });

    if (!allowed)
    {
        ctx.Response.StatusCode = 401;
        return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
    }

    user!.LastLoginAt = DateTimeOffset.UtcNow;
    await userStore.UpdateAsync(user);

    var claims = BuildClaims(user.Username, user.Role, user.MustChangePassword);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));

    return Results.Ok(new { role = user.Role.ToString(), mustChangePassword = user.MustChangePassword });
});

// GET /api/auth/me
app.MapGet("/api/auth/me", (HttpContext ctx) =>
{
    if (!ctx.User.Identity?.IsAuthenticated ?? true)
        return Results.Json(new { error = "Not authenticated" }, statusCode: 401);

    var username = ctx.User.FindFirstValue(ClaimTypes.Name);
    var role     = ctx.User.FindFirstValue(ClaimTypes.Role);
    var mustChange = ctx.User.FindFirstValue("must_change_password") == "true";
    var authMode   = ctx.User.FindFirstValue("auth_mode") ?? "password";
    return Results.Ok(new { username, role, mustChangePassword = mustChange, authMode });
});

// GET /api/auth/config — unauthenticated. Tells the SPA which doors exist so it can render the
// login screen without guessing. Exposes no secret: the client id is public by construction and
// this returns only booleans plus the provider's display name.
app.MapGet("/api/auth/config", () => Results.Ok(new
{
    oidcEnabled,
    oidcName,
    passwordLogin = passwordLoginEnabled,
}));

// GET /api/auth/oidc/start — begins the authorization-code flow. The handler owns
// /api/auth/oidc/callback; both sit under /api/auth/* and so are exempt from the authz chokepoint.
app.MapGet("/api/auth/oidc/start", () => oidcEnabled
    ? Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/" },
        [OpenIdConnectDefaults.AuthenticationScheme])
    : Results.Json(new { error = "SSO is not configured" }, statusCode: 404));

// POST /api/auth/logout
app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true });
});

// POST /api/auth/change-password
app.MapPost("/api/auth/change-password", async (HttpContext ctx, IServiceScopeFactory scopeFactory) =>
{
    if (!ctx.User.Identity?.IsAuthenticated ?? true)
        return Results.Json(new { error = "Not authenticated" }, statusCode: 401);

    var body = await ctx.Request.ReadFromJsonAsync<ChangePasswordDto>();
    if (body == null || string.IsNullOrWhiteSpace(body.NewPassword) || body.NewPassword.Length < 8)
        return Results.BadRequest(new { error = "New password must be at least 8 characters" });

    if (!config.Persist)
        return Results.Ok(new { ok = true });  // no-op in mock mode

    var username = ctx.User.FindFirstValue(ClaimTypes.Name)!;

    using var scope = scopeFactory.CreateScope();
    var userStore   = scope.ServiceProvider.GetRequiredService<IUserStore>();
    var user        = await userStore.FindByUsernameAsync(username);
    if (user == null) return Results.Json(new { error = "User not found" }, statusCode: 404);

    // Verify old password if the account is not in forced-rotation state.
    if (!user.MustChangePassword)
    {
        if (string.IsNullOrWhiteSpace(body.OldPassword) || !PasswordHasher.Verify(body.OldPassword, user.PasswordHash))
            return Results.Json(new { error = "Current password incorrect" }, statusCode: 400);
    }

    user.PasswordHash      = PasswordHasher.Hash(body.NewPassword);
    user.MustChangePassword = false;
    await userStore.UpdateAsync(user);

    await userStore.WriteAuditAsync(new AuditEventEntity
    {
        Timestamp     = DateTimeOffset.UtcNow,
        Username      = username,
        Verb          = "change_password",
        Allowed       = true,
        CorrelationId = ctx.TraceIdentifier,
    });

    // Re-issue the cookie without the must_change_password claim.
    var claims = BuildClaims(user.Username, user.Role, mustChangePassword: false);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));

    return Results.Ok(new { ok = true });
});

// ── API Routes ────────────────────────────────────────────────────────────────

app.MapGet("/api/state", (Store s, AppConfig cfg) => Results.Ok(new
{
    hosts = s.GetAllHosts(),
    totals = s.ComputeTotals(),
    diff = s.GetDiff(),
    credentialsSet = s.HasGlobalCreds,
    mockMode = cfg.MockMode,
    build = cfg.BuildStamp,
}));

app.MapPost("/api/quit", (Store s, IHostApplicationLifetime life) =>
{
    s.ClearAll();
    life.StopApplication();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/credentials", async (HttpContext ctx, Store s, EventHub h, IServiceScopeFactory scopeFactory) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<CredentialsDto>();
    if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
        return Results.BadRequest("username and password are required");

    s.SetGlobalCredentials(body.Username, body.Password);

    // Persist so credentials survive restarts (ENG-0002). Works with both AesGcmSecretStore and EphemeralSecretStore.
    using var scope = scopeFactory.CreateScope();
    var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
    await secretStore.SetAsync("global_winrm", JsonSerializer.Serialize(new { username = body.Username, password = body.Password }));

    h.Broadcast("credentialsSet", new { ok = true });
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/hosts", async (HttpContext ctx, Store s, EventHub h, AppConfig cfg, ProviderRegistry registry, IServiceScopeFactory scopeFactory) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<AddHostsDto>();
    if (body == null || string.IsNullOrWhiteSpace(body.Addresses))
        return Results.BadRequest("addresses required");

    var platform = Enum.TryParse<PlatformKind>(body.Platform, ignoreCase: true, out var pk) ? pk : PlatformKind.HyperV;

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

    var hostsToCheck = new List<VMentory.Core.Host>();
    foreach (var addr in addresses)
    {
        var host = new VMentory.Core.Host
        {
            Address             = addr,
            Fqdn                = addr,
            Platform            = platform,
            UseGlobalCreds      = platform == PlatformKind.Proxmox ? false : body.UseGlobalCreds,
            SkipTlsVerification = body.SkipTlsVerification,
            Connecting          = true,
        };
        if (platform == PlatformKind.Proxmox && !string.IsNullOrWhiteSpace(body.Token))
            host.PerHostCreds = new Credentials("", body.Token!);
        else if (!body.UseGlobalCreds && !string.IsNullOrWhiteSpace(body.Username))
            host.PerHostCreds = new Credentials(body.Username!, body.Password ?? "");

        s.AddHost(host);
        h.Broadcast("hostAdded", host);
        hostsToCheck.Add(host);
    }

    if (cfg.Persist && hostsToCheck.Count > 0)
    {
        using var scope  = scopeFactory.CreateScope();
        var invStore    = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
        var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        foreach (var host in hostsToCheck)
        {
            await invStore.UpsertRegistrationAsync(host);
            // Persist per-host creds (HyperV username/password or Proxmox API token) via ISecretStore (ENG-0002).
            if (host.PerHostCreds != null)
                await secretStore.SetAsync($"host_cred:{host.Id}",
                    JsonSerializer.Serialize(new { username = host.PerHostCreds.Username, password = host.PerHostCreds.GetPassword() }));
        }
    }

    _ = Task.Run(async () =>
    {
        foreach (var host in hostsToCheck)
        {
            var addr = host.Address;
            try
            {
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

                var icmp = await ReachabilityChecker.PingAsync(addr, TimeSpan.FromSeconds(2));

                // ── Proxmox path ──────────────────────────────────────────────
                if (host.Platform == PlatformKind.Proxmox)
                {
                    var apiPort = await ReachabilityChecker.TestTcpPortAsync(addr, 8006, TimeSpan.FromSeconds(3));
                    s.UpdateHost(host.Id, hh =>
                    {
                        hh.Reachability.CheckedAt = DateTimeOffset.UtcNow;
                        hh.Reachability.Icmp  = icmp;
                        hh.Reachability.WinRm = apiPort;
                    });
                    var afterPveReach = s.GetHost(host.Id);
                    if (afterPveReach != null)
                        h.Broadcast("reachability", new { hostId = host.Id, reachability = afterPveReach.Reachability });

                    if (!apiPort)
                    {
                        s.UpdateHost(host.Id, hh =>
                        {
                            hh.AddError   = !icmp ? "Host unreachable (ICMP failed)" : "PVE API port (8006) not responding";
                            hh.Connecting = false;
                        });
                        var afterErr = s.GetHost(host.Id);
                        if (afterErr != null) h.Broadcast("hostUpdated", afterErr);
                        continue;
                    }

                    var pveHost = s.GetHost(host.Id);
                    if (pveHost != null)
                    {
                        (bool ok, string err) = await registry.For(PlatformKind.Proxmox).QuickConnectAsync(pveHost);
                        s.UpdateHost(host.Id, hh => { if (!ok) hh.AddError = err; hh.Connecting = false; });
                    }
                    var pveFinal = s.GetHost(host.Id);
                    if (pveFinal != null) h.Broadcast("hostUpdated", pveFinal);
                    continue;
                }

                // ── HyperV path ───────────────────────────────────────────────
                var winrm = await ReachabilityChecker.TestTcpPortAsync(addr, cfg.WinRmPort, TimeSpan.FromSeconds(3));
                s.UpdateHost(host.Id, hh =>
                {
                    hh.Reachability.CheckedAt = DateTimeOffset.UtcNow;
                    hh.Reachability.Icmp  = icmp;
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
                        hh.AddError  = !icmp ? "Host unreachable (ICMP failed)" : "WinRM port not responding";
                        hh.Connecting = false;
                    });
                    DevLog.Warn($"[ADD]  {addr} — ICMP={icmp}, WinRM={winrm}");
                    var afterWinrm = s.GetHost(host.Id);
                    if (afterWinrm != null) h.Broadcast("hostUpdated", afterWinrm);
                    continue;
                }

                var creds = s.GetEffectiveCreds(host);
                if (creds == null)
                {
                    DevLog.Warn($"[ADD]  no credentials available for {addr}");
                    s.UpdateHost(host.Id, hh =>
                    {
                        hh.Reachability.Auth = AuthState.Unknown;
                        hh.AddError   = "No credentials configured — set global credentials first";
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
                    hh.Reachability.Auth        = authState;
                    hh.Reachability.ErrorDetail = authErr;
                });

                if (authState == AuthState.Ok)
                {
                    var storedHost = s.GetHost(host.Id);
                    if (storedHost != null)
                    {
                        (bool ok, string err) = await registry.For(PlatformKind.HyperV).QuickConnectAsync(storedHost);
                        s.UpdateHost(host.Id, hh => { if (!ok) hh.AddError = err; hh.Connecting = false; });
                    }
                }
                else
                {
                    DevLog.Err($"[ADD]  auth failed for {addr}: {authErr}");
                    s.UpdateHost(host.Id, hh =>
                    {
                        hh.AddError   = $"Authentication failed: {authErr}";
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

app.MapDelete("/api/hosts/{id}", async (string id, Store s, EventHub h, AppConfig cfg, IServiceScopeFactory scopeFactory) =>
{
    if (!s.RemoveHost(id)) return Results.NotFound();

    if (cfg.Persist)
    {
        using var scope  = scopeFactory.CreateScope();
        var invStore    = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
        var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await invStore.RemoveAsync(id);
        await secretStore.DeleteAsync($"host_cred:{id}");
    }

    h.Broadcast("hostRemoved", new { hostId = id });
    return Results.Ok(new { ok = true });
});

// Edit an already-registered host: rename (DisplayName) and/or re-enter credentials. All fields
// optional — only what's provided is changed. Secrets are write-only (a blank token/password keeps
// the current one). Credential/TLS changes trigger an immediate reachability re-check.
app.MapMethods("/api/hosts/{id}", ["PATCH"], async (string id, HttpContext ctx, Store s, EventHub h, AppConfig cfg, ProviderRegistry registry, IServiceScopeFactory scopeFactory) =>
{
    var host = s.GetHost(id);
    if (host == null) return Results.NotFound();

    EditHostDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<EditHostDto>(); }
    catch { return Results.BadRequest(new { error = "invalid body" }); }
    if (body == null) return Results.BadRequest(new { error = "body required" });

    var newToken = host.Platform == PlatformKind.Proxmox && !string.IsNullOrWhiteSpace(body.Token) ? body.Token!.Trim() : null;
    var hvCredsProvided = host.Platform == PlatformKind.HyperV && !string.IsNullOrWhiteSpace(body.Username);

    if (newToken != null && !System.Text.RegularExpressions.Regex.IsMatch(newToken, @"^[^@\s]+@[^!\s]+![^=\s]+=\S+$"))
        return Results.BadRequest(new { error = "API token must look like user@realm!tokenid=secret" });

    s.UpdateHost(id, hh =>
    {
        if (body.DisplayName != null)
            hh.DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim();
        if (body.SkipTlsVerification.HasValue)
            hh.SkipTlsVerification = body.SkipTlsVerification.Value;
        if (body.UseGlobalCreds.HasValue && host.Platform == PlatformKind.HyperV)
            hh.UseGlobalCreds = body.UseGlobalCreds.Value;
        if (newToken != null)
        {
            hh.PerHostCreds?.Dispose();
            hh.PerHostCreds = new Credentials("", newToken);
            hh.UseGlobalCreds = false;
        }
        else if (hvCredsProvided)
        {
            hh.PerHostCreds?.Dispose();
            hh.PerHostCreds = new Credentials(body.Username!.Trim(), body.Password ?? "");
            hh.UseGlobalCreds = false;
        }
    });

    var credsChanged = newToken != null || hvCredsProvided;
    var updated = s.GetHost(id)!;

    if (cfg.Persist)
    {
        using var scope  = scopeFactory.CreateScope();
        var invStore    = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
        var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await invStore.UpsertRegistrationAsync(updated);
        if (credsChanged && updated.PerHostCreds != null)
            await secretStore.SetAsync($"host_cred:{id}",
                JsonSerializer.Serialize(new { username = updated.PerHostCreds.Username, password = updated.PerHostCreds.GetPassword() }));
    }

    h.Broadcast("hostUpdated", updated);

    // Re-verify connectivity when creds or TLS handling changed, so the new status shows immediately.
    if (credsChanged || body.SkipTlsVerification.HasValue)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                s.UpdateHost(id, hh => hh.Connecting = true);
                var conn = s.GetHost(id);
                if (conn != null) h.Broadcast("hostUpdated", conn);

                var target = s.GetHost(id);
                if (target != null)
                {
                    var (ok, err) = await registry.For(target.Platform).QuickConnectAsync(target);
                    s.UpdateHost(id, hh => { hh.AddError = ok ? "" : err; hh.Connecting = false; });
                }
                var done = s.GetHost(id);
                if (done != null) h.Broadcast("hostUpdated", done);
            }
            catch (Exception ex)
            {
                logWriter.LogError($"Edit re-check failed for host {id}", ex);
                s.UpdateHost(id, hh => { hh.Connecting = false; hh.AddError = "Unexpected error during connect"; });
                var failed = s.GetHost(id);
                if (failed != null) h.Broadcast("hostUpdated", failed);
            }
        });
    }

    return Results.Ok(new { ok = true });
});

app.MapPost("/api/scan", async (HttpContext ctx, Store s, EventHub h, AppConfig cfg, ProviderRegistry registry, IServiceScopeFactory scopeFactory) =>
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

    List<VMentory.Core.Host> previous;
    {
        using var scope  = scopeFactory.CreateScope();
        var invStore = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
        previous = await invStore.GetLatestSnapshotHostsAsync(s.GetAllHosts().Select(hh => hh.Id));
    }

    var sem   = new SemaphoreSlim(3, 3);
    var tasks = hosts.Select(async host =>
    {
        await sem.WaitAsync();
        try
        {
            s.UpdateHost(host.Id, h => { h.ScanState = ScanState.Scanning; h.ScanError = ""; });
            h.Broadcast("scanProgress", new { hostId = host.Id, state = "scanning" });

            var creds = s.GetEffectiveCreds(host);
            if (creds == null && host.Platform == PlatformKind.HyperV)
            {
                s.UpdateHost(host.Id, h => { h.ScanState = ScanState.Error; h.ScanError = "No credentials"; });
                h.Broadcast("scanProgress", new { hostId = host.Id, state = "error", error = "No credentials" });
                return;
            }

            var (ok, err) = await registry.For(host.Platform).ScanAsync(host);
            var now = DateTimeOffset.UtcNow;

            s.UpdateHost(host.Id, hh =>
            {
                if (ok)
                {
                    hh.ScanState  = ScanState.Done;
                    hh.LastScanned = now;
                    hh.ScanError  = "";
                    hh.Fqdn            = host.Fqdn;
                    hh.OsCaption       = host.OsCaption;
                    hh.OsVersion       = host.OsVersion;
                    hh.LastBoot        = host.LastBoot;
                    hh.Manufacturer    = host.Manufacturer;
                    hh.Model           = host.Model;
                    hh.Serial          = host.Serial;
                    hh.CpuModel        = host.CpuModel;
                    hh.SocketCount     = host.SocketCount;
                    hh.TotalCores      = host.TotalCores;
                    hh.TotalLogicalProcs = host.TotalLogicalProcs;
                    hh.TotalRamGb      = host.TotalRamGb;
                    hh.Volumes         = host.Volumes;
                    hh.Vms             = host.Vms;
                }
                else
                {
                    hh.ScanState = ScanState.Error;
                    hh.ScanError = err;
                }
            });

            if (ok)
            {
                var scanned = s.GetHost(host.Id);
                if (scanned != null)
                {
                    using var scope  = scopeFactory.CreateScope();
                    var invStore = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
                    await invStore.SaveSnapshotAsync(scanned);
                }
            }

            h.Broadcast("scanProgress", new
            {
                hostId = host.Id,
                state  = ok ? "done" : "error",
                error  = ok ? null : err,
                host   = s.GetHost(host.Id),
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

// Estate dashboard + remediation tracker (ENG-0015) — see EstateEndpoints.cs.
app.MapEstateEndpoints();

// SSE: cookies are sent automatically by the browser on same-origin GET requests — no ?token= needed.
app.MapGet("/api/events", async (HttpContext ctx, IHostApplicationLifetime lifetime) =>
{
    if (!ctx.User.Identity?.IsAuthenticated ?? true)
    {
        ctx.Response.StatusCode = 401;
        return;
    }

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
        ctx.RequestAborted, lifetime.ApplicationStopping);

    var clientId = hub.Subscribe();
    await hub.StreamAsync(clientId, ctx.Response, cts.Token);
});

app.MapGet("/api/export/json", (Store s) =>
{
    var data = Exporter.ToJson(s.GetAllHosts(), s.ComputeTotals(), s.GetDiff());
    return Results.File(data, "application/json",
        $"VMentory-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
});

app.MapGet("/api/export/csv", (Store s) =>
{
    var data = Exporter.ToCsvZip(s.GetAllHosts());
    return Results.File(data, "application/zip",
        $"VMentory-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
});

// ── Start + run ──────────────────────────────────────────────────────────────

var scheme      = config.HttpOnly ? "http" : "https";
var displayHost = config.HttpAddr is "0.0.0.0" or "::" ? "localhost" : config.HttpAddr;
var url         = $"{scheme}://{displayHost}:{config.Port}/";

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine(@"
  ╔══════════════════════════════════════════════╗
  ║                  VMentory                    ║
  ╚══════════════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine($"  Version : {AppVersion()}");
Console.WriteLine($"  Build   : {config.BuildStamp}");
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
Console.WriteLine($"\n  Bind    : {config.HttpAddr}:{config.Port}");
Console.WriteLine($"  TLS     : {tlsSource}");
if (config.Persist)
    Console.WriteLine($"  Secrets : {(kek != null ? "AES-256-GCM (persistent)" : "ephemeral — set VMENTORY_KEK (base64 32 bytes) to persist credentials across restarts")}");

if (!config.HttpOnly && tlsSource.StartsWith("self-signed"))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  [TLS]  — self-signed certificate; browsers will warn. Mount an operator cert");
    Console.WriteLine("           via VMENTORY_TLS_PFX or VMENTORY_TLS_CERT_PEM/VMENTORY_TLS_KEY_PEM.");
    Console.ResetColor();
}
Console.WriteLine($"\n  URL   : {url}");

await app.StartAsync();

var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
if (!Console.IsInputRedirected)
{
    Console.WriteLine("  Press Q to quit\n");
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
store.ClearAll();
Console.WriteLine("  Goodbye.");

// ── Helpers ───────────────────────────────────────────────────────────────────

// Returns the assembly version string (e.g. "1.0.0").
static string AppVersion()
{
    var v = typeof(AppConfig).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "1.0.0";
    var plus = v.IndexOf('+');
    return plus >= 0 ? v[..plus] : v;
}

// Reads the build-stamp.txt written by the Dockerfile build stage (UTC ISO timestamp via `date`)
// and formats it as v{YY}.{MM}.{DD}.{HHMM} in Africa/Johannesburg (SAST = UTC+2).
// Falls back to "dev" when running outside Docker (no build-stamp.txt present).
static string ComputeBuildStamp()
{
    try
    {
        var stampFile = Path.Combine(AppContext.BaseDirectory, "build-stamp.txt");
        if (!File.Exists(stampFile)) return "dev";
        var raw = File.ReadAllText(stampFile).Trim();
        if (raw == "dev" || string.IsNullOrEmpty(raw)) return "dev";
        if (!DateTimeOffset.TryParse(raw, out var dt)) return raw;
        var sast = TimeZoneInfo.GetSystemTimeZones()
            .FirstOrDefault(z => z.Id is "Africa/Johannesburg" or "South Africa Standard Time")
            ?? TimeZoneInfo.CreateCustomTimeZone("SAST", TimeSpan.FromHours(2), "SAST", "SAST");
        var local = TimeZoneInfo.ConvertTime(dt, sast);
        return $"v{local:yy}.{local:MM}.{local:dd}.{local:HHmm}";
    }
    catch { return "dev"; }
}

// Reload persisted credentials (ENG-0002). Runs after host registry load at startup.
// Restores global WinRM creds + per-host creds that were saved via ISecretStore.
static async Task LoadPersistedCredsAsync(Store store, ISecretStore secretStore)
{
    // Global WinRM credentials
    var globalJson = await secretStore.GetAsync("global_winrm");
    if (!string.IsNullOrEmpty(globalJson))
    {
        try
        {
            var c = JsonSerializer.Deserialize<StoredCred>(globalJson);
            if (c?.Username != null && c.Password != null)
            {
                store.SetGlobalCredentials(c.Username, c.Password);
                DevLog.Ok("[SECRETS] Global WinRM credentials restored from secret store.");
            }
        }
        catch { /* corrupt entry — skip */ }
    }

    // Per-host credentials
    foreach (var host in store.GetAllHosts().Where(h => !h.UseGlobalCreds))
    {
        var credJson = await secretStore.GetAsync($"host_cred:{host.Id}");
        if (string.IsNullOrEmpty(credJson)) continue;
        try
        {
            var c = JsonSerializer.Deserialize<StoredCred>(credJson);
            if (c?.Username != null && c.Password != null)
            {
                store.UpdateHost(host.Id, h => h.PerHostCreds = new Credentials(c.Username, c.Password));
                DevLog.Ok($"[SECRETS] Per-host credentials restored for {host.Address}.");
            }
        }
        catch { /* corrupt entry — skip */ }
    }
}

// First-admin seed (ENG-0008): runs on startup when no users exist. Reads
// VMENTORY_ADMIN_USER (default "admin") + VMENTORY_ADMIN_PASSWORD. If no password is configured
// a one-time password is generated and printed to stdout (Coolify captures container logs).
// MustChangePassword is always true for the seeded account — forced rotation on first login.
static async Task SeedAdminUserAsync(IUserStore userStore)
{
    if (await userStore.AnyUsersAsync()) return;

    var username = EnvOr("VMENTORY_ADMIN_USER", "admin");
    var password = Environment.GetEnvironmentVariable("VMENTORY_ADMIN_PASSWORD");
    var generated = false;

    if (string.IsNullOrWhiteSpace(password))
    {
        password  = GenerateToken();
        generated = true;
    }

    await userStore.CreateAsync(new AppUserEntity
    {
        Username          = username,
        PasswordHash      = PasswordHasher.Hash(password),
        Role              = AppRole.Admin,
        MustChangePassword = true,
        CreatedAt         = DateTimeOffset.UtcNow,
    });

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n  [SETUP] First-run admin account created: {username}");
    if (generated)
    {
        Console.WriteLine($"  [SETUP] Auto-generated password (change on first login): {password}");
        Console.WriteLine("          Set VMENTORY_ADMIN_USER + VMENTORY_ADMIN_PASSWORD to configure.");
    }
    else
    {
        Console.WriteLine("  [SETUP] Password set from VMENTORY_ADMIN_PASSWORD. Login and change it.");
    }
    Console.ResetColor();
}

// Audit an SSO sign-in attempt. Fail-safe by design: auditing must never be the reason a login
// breaks, so every failure here is swallowed (same posture as WriteAuditIfPossible).
static async Task RecordOidcAudit(HttpContext http, string username, bool allowed)
{
    try
    {
        var sf = http.RequestServices.GetService<IServiceScopeFactory>();
        if (sf == null) return;
        using var s = sf.CreateScope();
        var us = s.ServiceProvider.GetService<IUserStore>();
        if (us == null) return;
        await us.WriteAuditAsync(new AuditEventEntity
        {
            Timestamp     = DateTimeOffset.UtcNow,
            Username      = username,
            Verb          = "login_oidc",
            Allowed       = allowed,
            CorrelationId = http.TraceIdentifier,
        });
    }
    catch { /* never break sign-in over an audit write */ }
}

static List<Claim> BuildClaims(string username, AppRole role, bool mustChangePassword) =>
[
    new(ClaimTypes.Name, username),
    new(ClaimTypes.Role, role.ToString()),
    new("must_change_password", mustChangePassword ? "true" : "false"),
];

static async Task WriteAuditIfPossible(HttpContext ctx, string verb, bool allowed)
{
    try
    {
        var scope = ctx.RequestServices.GetService<IServiceScopeFactory>();
        if (scope == null) return;
        using var s = scope.CreateScope();
        var us = s.ServiceProvider.GetService<IUserStore>();
        if (us == null) return;
        await us.WriteAuditAsync(new AuditEventEntity
        {
            Timestamp     = DateTimeOffset.UtcNow,
            Username      = ctx.User.FindFirstValue(ClaimTypes.Name),
            Verb          = verb,
            Allowed       = allowed,
            CorrelationId = ctx.TraceIdentifier,
            Detail        = ctx.Request.Path,
        });
    }
    catch { /* audit must never crash the request */ }
}

static byte[] LoadEmbeddedHtml()
{
    var asm  = System.Reflection.Assembly.GetEntryAssembly()!;
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

static int ResolvePort()
{
    var v = Environment.GetEnvironmentVariable("VMENTORY_HTTP_PORT");
    if (int.TryParse(v, out var p) && p is > 0 and < 65536) return p;
    return EnvFlag("VMENTORY_HTTP_ONLY") ? 8080 : 8443;
}

static string ResolveDataDir()
{
    var fromEnv = Environment.GetEnvironmentVariable("VMENTORY_DB");
    var dir = !string.IsNullOrWhiteSpace(fromEnv)
        ? (Path.GetDirectoryName(Path.GetFullPath(fromEnv)) ?? "")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMentory");
    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    return dir;
}

static string ResolveDbPath(string dataDir)
{
    var fromEnv = Environment.GetEnvironmentVariable("VMENTORY_DB");
    if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
    return Path.Combine(dataDir, "vmentory.db");
}

static string ResolveLogPath()
{
    try
    {
        var env = Environment.GetEnvironmentVariable("VMENTORY_LOG");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var db  = Environment.GetEnvironmentVariable("VMENTORY_DB");
        var dir = !string.IsNullOrWhiteSpace(db)
            ? Path.GetDirectoryName(Path.GetFullPath(db))
            : AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = AppContext.BaseDirectory;
        return Path.Combine(dir, "errors.log");
    }
    catch { return Path.Combine(AppContext.BaseDirectory, "errors.log"); }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

record LoginDto(string Username, string Password);
record ChangePasswordDto(string NewPassword, string? OldPassword = null);
record CredentialsDto(string Username, string Password);
record AddHostsDto(string Addresses, bool UseGlobalCreds = true, string? Username = null, string? Password = null,
    string Platform = "HyperV", string? Token = null, bool SkipTlsVerification = false);
// Edit an existing host. All nullable → only provided fields change; blank secrets keep the current ones.
record EditHostDto(string? DisplayName = null, string? Username = null, string? Password = null,
    string? Token = null, bool? SkipTlsVerification = null, bool? UseGlobalCreds = null);
record ScanBody(string? HostId);
record StoredCred(string? Username, string? Password);

// ── App config ────────────────────────────────────────────────────────────────

public class AppConfig
{
    public bool MockMode    { get; init; }
    public bool NoUpdate    { get; init; }  // kept for CLI compat
    public string BuildStamp { get; init; } = "dev";
    public bool VerboseMode { get; init; }
    public string HttpAddr  { get; init; } = "0.0.0.0";
    public int Port         { get; init; }
    public bool HttpOnly    { get; init; }
    public int WinRmPort    { get; set; } = 5985;
    public bool Persist     { get; init; }
    public string DataDir   { get; init; } = "";
    public string DbPath    { get; init; } = "";
}

// ── Error logger ──────────────────────────────────────────────────────────────

public class ErrorLogger : IDisposable
{
    private readonly StreamWriter? _writer;
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
            catch { }
        }
    }

    public void Dispose() => _writer?.Dispose();
}
