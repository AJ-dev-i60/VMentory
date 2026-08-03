# VMentory

> **🎯 PRIMARY FOCUS (re-focused 2026-08-03): VMentory is a monitoring tool for Hyper-V *and* Proxmox
> hosts.** Observe is **the product**, not the foundation beneath the other pillars — taken to depth on
> **both** platforms before management, Deploy or Migrate get further effort (ENG-0007 amendment).
> Scope is bounded: **health + inventory, no time-series, no trend charts, no alerting.**
>
> **⚠ The blocker this rests on (ENG-0013): Hyper-V is *wholly unreachable* from the containerized
> Core** — `Reachability.cs:169` shells `powershell.exe`, `:20` shells `ping.exe`, `:52,80-82` ensure
> the *local* WinRM service + `TrustedHosts`, and `Scanner.cs:216` runs `Invoke-Command -ComputerName`
> (Core is assumed to *be* the domain WinRM client). On Linux only `Program.cs:517`'s TCP probe
> survives. **Resolved by SSH + PowerShell on the host**, via **one shared `ISshExecutor`** that also
> serves the Proxmox residue. **Not yet proven against a live HV host — that is the slice gate.**
>
> **Next four slices:** (5) named credentials `ENG-0012` → (6) health model `ENG-0011a` →
> (7) Hyper-V over SSH `ENG-0013` → (8) multi-platform monitoring dashboard. Proxmox **write verbs are
> demoted** to (9); the **HV agent + private CA are off every near-term path**, justified now only by
> migration (the last-weighted pillar).
>
> **Phase 2 foundation complete (re-baselined slices (1)–(4) all built; (1)–(3) deployed).** This
> document still describes the shipping Phase-1 app (below); Phase 2 turns VMentory into a
> **container-based, single-operator, multi-platform (Hyper-V + Proxmox) platform** — **four pillars on
> one shared foundation** (ENG-0006, Proxmox-first per ENG-0007, **amended to monitoring-first**). The
> foundation is a **hosted, web-first container** (ENG-0010): Core binds `0.0.0.0`, terminates HTTPS
> itself, ships **login + RBAC** (ENG-0008), `ISecretStore` (ENG-0002), persistence, and a general
> operations engine. **The end user installs nothing** — **neither platform runs an agent for
> monitoring**: Proxmox via native REST API + constrained SSH key (ENG-0009), Hyper-V via SSH +
> PowerShell (ENG-0013). **Already built:** containerized Core + hosted bootstrap (slice (1)), login +
> RBAC with cookie auth + `RbacCatalog` (slice (2)), `ISecretStore` AES-256-GCM (slice (3)), Proxmox
> API read provider (slice (4), live against vega14), and EF Core/SQLite persistence (original slice 3)
> — deployed at https://vmentorydev.edgestudios.co.za. Start at
> [`docs/phase2/PROGRESS.md`](docs/phase2/PROGRESS.md) → `ARCHITECTURE.md` / `ROADMAP.md`, and
> `docs/engineering/REGISTER.md` for the decision register. Agents live in `.claude/agents/`.

Inventories Hyper-V hosts over WinRM and serves a web dashboard. **As of 2.0 slice (1) the bootstrap is
a hosted service** — binds `0.0.0.0:{configurable}` and terminates HTTPS at Core (ENG-0010), packaged as
a Linux container — not the Phase-1 single-exe loopback desktop app. (The Windows single-exe still builds
via `build.ps1` for the legacy desktop path.)

- **Repo**: https://github.com/AJ-dev-i60/VMentory
- **Stack**: ASP.NET Core 8 minimal API · vanilla JS SPA (no framework) · EF Core 8 + SQLite · AES-256-GCM secret store · PBKDF2-SHA256 auth · PowerShell subprocess for WinRM · custom Canvas donut charts

---

## UI/UX work — read this first

A separate Claude session drives the visual design. The shared workspace lives at **`design/`** (not embedded in the binary). Before doing any UI work in `wwwroot/index.html`:

1. **Read `design/README.md`** — defines the workflow and request templates.
2. **Check `design/STATUS.md`** — the live board of proposed / in-progress / awaiting-design items.
3. **Check `design/requests/from-design/`** — implementation specs waiting for you. Each request lists the mockup, what to change, and what's out of scope.
4. **Open the linked mockup** in a browser before coding. The mockups are standalone HTML in `design/mockups/` using the same design tokens as the live app.

When you need a design answer mid-implementation, **don't guess** — drop a markdown file in `design/requests/from-codebase/` (template in `design/README.md`), update `STATUS.md`, commit with `design: question about X`, and move to other work while you wait.

Update `STATUS.md` when you start a request (move to *In progress*) and when you ship it (move to *Shipped* with commit SHA). Commit implementation work with the normal `feat:` / `fix:` prefix, not `design:`.

---

## File map

| File | Purpose |
|---|---|
_**Phase-2 layout (2.0 slice 1):** the solution `VMentory.sln` has two projects — **`VMentory.Core`** (classlib, `namespace VMentory.Core`, holds the domain model `Models.cs`) and **`VMentory.Web`** (the exe at repo root, `namespace VMentory.Web`, references Core; everything below except `Models.cs`). `Providers.*` / `Agent` projects come in later 2.0 slices._

| `Program.cs` | Entry point: API routes, config, hosted bootstrap (0.0.0.0 + Kestrel HTTPS), `ErrorLogger` |
| `TlsSetup.cs` | Resolves the Core-terminated HTTPS cert (ENG-0010): operator PFX → operator PEM → self-signed fallback (cached in `DataDir` in real mode) |
| `Dockerfile` / `.dockerignore` | Single Linux container image (ENG-0010): .NET app + SSH client, no `qemu`/`qm`; runs as non-root, DB + cert on the `/data` volume |
| `VMentory.Core/Models.cs` | All data types: `Host` (incl. `Platform` discriminator), `Vm`, `Vhd`, `Volume`, `Credentials`, enums (in `VMentory.Core`) |
| `VMentory.Core/IVirtualizationProvider.cs` | Provider abstraction (`Platform`, `Capabilities`, `QuickConnectAsync`, `ScanAsync`) — the Core↔platform seam |
| `VMentory.Core/ProviderCapability.cs` | `[Flags]` capability enum + `ProviderCapabilities` (gates UI + ops engine; HV mgmt verbs allowed per ENG-0007) |
| `VMentory.Core/PlatformKind.cs` | `HyperV` / `Proxmox` discriminator |
| `VMentory.Core/Persistence/` | EF Core SQLite layer: `VMentoryDbContext`, entities (`HostRegistrationEntity`, `InventorySnapshotEntity`, `AppUserEntity`, `AuditEventEntity`, `SecretEntity`, `DekEntity`), `IInventoryStore`/`EfInventoryStore`, `IUserStore`/`EfUserStore`, design-time factory |
| `VMentory.Core/Auth/` | `AppRole` enum, `ConsolePermission` flags enum, `PasswordHasher` (PBKDF2-SHA256 BCL-only) |
| `VMentory.Core/Secrets/` | `ISecretStore`, `AesGcmSecretStore` (AES-256-GCM SQLite, ENG-0002), `EphemeralSecretStore` (in-memory), `DekProvider` (KEK/DEK manager) |
| `VMentory.Core/Migrations/` | EF Core migrations (`Initial`, `AddAuth`, `AddSecrets`); applied via `Database.Migrate()` at startup |
| `RbacCatalog.cs` | Static role→capability mapping (Admin/VmOperator/BackupOperator/Viewer over `ProviderCapability`+`ConsolePermission`) |
| `GlobalUsings.cs` | Project-wide `global using Host = VMentory.Core.Host;` alias (resolves the domain-vs-framework `Host` ambiguity — see gotcha #2) |
| `HyperVProvider.cs` | `IVirtualizationProvider` for Hyper-V; wraps `Scanner`/`Reachability`, resolves creds from `Store` |
| `Store.cs` | Thread-safe in-memory working set (`ConcurrentDictionary`), diff, totals |
| `Scanner.cs` | WinRM full-inventory scan + quick-connect via PowerShell `Invoke-Command` |
| `Reachability.cs` | Ping / TCP / WinRM-auth checks + `RunPowerShellAsync` helper |
| `Poller.cs` | `BackgroundService`: reachability re-check every 30 s |
| `EventHub.cs` | SSE broadcast via `System.Threading.Channels` |
| `MockData.cs` | 5 fake hosts for `--mock` mode |
| `Exporter.cs` | CSV zip + JSON export |
| `wwwroot/index.html` | Entire SPA (CSS + JS inline; login + change-password walls, RBAC-aware UI) |
| `VMentory.Web.csproj` | SDK Web project (the exe); `AssemblyName=VMentory`; `Version` defaults to `1.0.0`; references `VMentory.Core` |
| `VMentory.Core/VMentory.Core.csproj` | Classlib SDK project; domain model |
| `VMentory.sln` | Solution tying `VMentory.Web` + `VMentory.Core` together |
| `build.ps1` | Release build: `dotnet publish` win-x64 single-file → `dist\VMentory.exe` |
| `.github/workflows/release.yml` | CI: push `v*` tag → build → GitHub Release with `VMentory.exe` asset |
| `.github/workflows/ci.yml` | CI build check: triggers on push/PR to `dev` and `main`; runs `dotnet restore` + `dotnet build VMentory.sln -c Release` |

---

## Dev commands

```powershell
dotnet build VMentory.sln                                # compile check (whole solution)
# Dev run — the .exe apphost requires elevation (app.manifest); run the built DLL to avoid the UAC prompt:
dotnet bin\Debug\net8.0\VMentory.dll --mock              # dev mode: 5 fake hosts, no real WinRM
.\build.ps1                         # release exe → dist\VMentory.exe
.\build.ps1 -Version 1.2.0          # embed specific version number

# Container (ENG-0010): single Linux image, Core-terminated HTTPS on a mounted volume
docker build -t vmentory:dev .
docker run --rm -p 8443:8443 -v vmentory-data:/data \
  -e VMENTORY_HTTP_ONLY=1 \
  -e VMENTORY_ADMIN_PASSWORD=changeme \
  vmentory:dev
```

**Hosted runtime (ENG-0010) — env contract:** `VMENTORY_HTTP_ADDR` (default `0.0.0.0`),
`VMENTORY_HTTP_PORT` (default 8443 HTTPS / 8080 HTTP), `VMENTORY_HTTP_ONLY=1` (plain HTTP behind a
reverse proxy / for dev), `VMENTORY_TLS_PFX` (+`_PASSWORD`) or `VMENTORY_TLS_CERT_PEM`+`VMENTORY_TLS_KEY_PEM`
(operator cert; nothing baked into the image), `VMENTORY_DB` (SQLite path → the `/data` volume; default
`%LocalAppData%\VMentory\vmentory.db` outside Docker), `VMENTORY_ADMIN_USER` (default `admin`) +
`VMENTORY_ADMIN_PASSWORD` (first-admin seed; auto-generated + printed to stdout if not set, always
`MustChangePassword=true`), `VMENTORY_KEK` (base64 32-byte key; if set, credentials are AES-256-GCM
encrypted in SQLite and persist across restarts; if absent, ephemeral), `VMENTORY_LOG` (error log path;
defaults to the `VMENTORY_DB` directory → `/data/errors.log` in the container; `ErrorLogger` is
fail-safe — a bad path degrades to no-op). `GET /health` is an unauthenticated liveness probe. Behind a
reverse proxy (Coolify/Traefik), set `VMENTORY_HTTP_ONLY=1`.

## Release workflow

```powershell
git add -p && git commit -m "feat: ..."
git tag v1.2.0
git push && git push --tags         # Actions builds and publishes the release automatically
```

**Auto-deploy (GitHub → Coolify):** A GitHub webhook on `AJ-dev-i60/VMentory` fires on every push; the
request is signed via HMAC-SHA256 (`X-Hub-Signature-256`). Coolify filters on `git_branch = dev` and
rebuilds the container automatically. `VMENTORY_ADMIN_PASSWORD` is set in the Coolify environment so the
first-admin seed gets a stable password on volume resets. The CI build check (`.github/workflows/ci.yml`)
runs `dotnet restore` + `dotnet build VMentory.sln -c Release` on every push/PR to `dev` and `main`.

---

## API surface

| Method | Path | Description |
|---|---|---|
| GET | `/health` | Unauthenticated liveness probe — `{status, version, build}` |
| POST | `/api/auth/login` | Cookie-based login — `{username, password}` → sets `vmentory_session` cookie |
| GET | `/api/auth/me` | Current user — `{username, role, mustChangePassword}` |
| POST | `/api/auth/logout` | Clears the session cookie |
| POST | `/api/auth/change-password` | Change password (forced on first login) |
| GET | `/api/state` | Full snapshot: `hosts, totals, diff, credentialsSet, mockMode, build` |
| POST | `/api/credentials` | Set global WinRM credentials (persisted via `ISecretStore`) |
| POST | `/api/hosts` | Add hosts (DNS + reachability + quick connect) |
| PATCH | `/api/hosts/{id}` | Edit a host: rename (`displayName`) and/or re-enter credentials (`token` / `username`+`password`, `skipTlsVerification`, `useGlobalCreds`). All fields optional; blank secrets keep current. Cred/TLS change triggers a reachability re-check |
| DELETE | `/api/hosts/{id}` | Remove host |
| POST | `/api/scan` | Trigger full inventory (max 3 concurrent, fire-and-forget) |
| GET | `/api/events` | SSE stream (cookie auth — EventSource sends cookies on same-origin GET) |
| GET | `/api/export/json` | Download JSON export |
| GET | `/api/export/csv` | Download CSV zip |
| POST | `/api/quit` | Graceful shutdown (zeroes in-memory secrets; persisted data is kept) |

All `/api/*` routes (except `/api/auth/*`) require the `vmentory_session` cookie set by `/api/auth/login`.

> **Forthcoming (ENG-0012, planned — slice-5 era, NOT yet built).** Credentials become first-class
> named entities. When that slice lands, this table changes: `POST /api/credentials` is **repurposed**
> from the global-WinRM setter to a credential **create** endpoint; new `GET /api/credentials`,
> `GET/POST /api/credentials/{id}/rotate`, `DELETE /api/credentials/{id}` (metadata-only responses,
> write-only rotation, 409 + `usedByHosts` on a referenced delete); and `POST /api/hosts` /
> `PATCH /api/hosts/{id}` reference `managementCredentialId` (+ optional `transportCredentialId`)
> instead of raw `username`/`password`/`token`/`useGlobalCreds`. Global creds + `Host.UseGlobalCreds`
> are retired. See [`docs/phase2/specs/persistence-and-security.md` §4a](docs/phase2/specs/persistence-and-security.md).
> Separately, ENG-0011a (planned) adds a per-host `health` object (tier + faults[]) to `/api/state` +
> SSE, superseding the loose `Reachability` booleans — see [`docs/phase2/specs/provider-abstraction.md` §8](docs/phase2/specs/provider-abstraction.md).

---

## Non-obvious gotchas

1. **PowerShell scripts use `string.Format()`** — never raw string literals (`$"""..."""`). C# interpolation braces conflict with PowerShell `{}` blocks. See `Scanner.cs:BuildRemoteWrapper` and `Reachability.cs`.

2. **`Host` ambiguity (domain vs `Microsoft.Extensions.Hosting.Host`)** — resolved project-wide by `GlobalUsings.cs` (`global using Host = VMentory.Core.Host;`). `Program.cs` still spells out `VMentory.Core.Host` in three spots (the `hostsToCheck` list, the `AddHost` call, the snapshot lambda) — harmless and explicit. New code can rely on the bare `Host` alias.

3. **HTML served via `Assembly.GetManifestResourceStream()`** — not `StaticFiles` or `ManifestEmbeddedFileProvider` (that requires a NuGet package unavailable offline). See `LoadEmbeddedHtml()` in `Program.cs` and `<EmbeddedResource>` in the csproj.

4. **`ErrorLogger` uses `FileShare.ReadWrite`** — allows two dev instances to share the log without crashing.

5. **Publish flags are CLI-only** — adding `-r win-x64` to the csproj breaks offline `dotnet restore`. Always pass via `build.ps1` or explicit `dotnet publish` flags.

6. **`Updater.cs` is deleted** — the Windows exe auto-updater was dead code in container mode. `--no-update` is still accepted as a CLI arg for compatibility but does nothing.

7. **Persistence: always-on in real mode, ephemeral in `--mock`.** SQLite via EF Core. DB path = `VMENTORY_DB` env var (the container points this at a mounted volume) else `%LocalAppData%\VMentory\vmentory.db`. **`--mock` registers no DB and writes nothing to disk.** Host registry + inventory snapshots + users + auth audit + encrypted secrets persist. Credentials (`ISecretStore`) persist when `VMENTORY_KEK` is set; without it, `EphemeralSecretStore` is used (creds survive the process, lost on restart). The in-memory `Store` is still the working set; `IInventoryStore` is write-through (add/remove host, snapshot-on-scan) and seeds `Store` at startup. `LoadPersistedCredsAsync` restores credentials from the secret store after host registry is loaded.

8. **EF Core / SQLite gotchas:** (a) SQLite **can't `ORDER BY` a `DateTimeOffset`** — order snapshots by the autoincrement `Id` (higher = newer), not `TakenAt`. (b) Migrations live in `VMentory.Core`; **rebuild after `dotnet ef migrations add`** or the running DLL won't contain the new migration and `Migrate()` creates an empty DB. (c) The single-file exe bundles the SQLite native lib via the existing `IncludeNativeLibrariesForSelfExtract=true` — verified working. (d) Generate migrations with `dotnet ef migrations add <Name> --project VMentory.Core --startup-project VMentory.Core` (a design-time factory avoids running the web app).

9. **Hosted bootstrap (ENG-0010).** `Program.cs` binds `0.0.0.0:{VMENTORY_HTTP_PORT}` via `ConfigureKestrel` and terminates HTTPS itself (`TlsSetup.ResolveServerCertificate`). (a) The **console Q-to-quit loop only runs when `!Console.IsInputRedirected`** — in a container (no TTY) it's skipped and the operator stops via SIGTERM. (b) The **`app.manifest` (requireAdministrator) is `Condition="'$(OS)' == 'Windows_NT'"`** so the Linux container publish doesn't fail. (c) WinRM-service ensure is **Windows-only-guarded** (`OperatingSystem.IsWindows()`). (d) Self-signed cert is **cached in `DataDir`** in real mode; mock is ephemeral.

10. **Build stamp:** `Dockerfile` build stage runs `git log -1 --format=%cI HEAD > /app/build-stamp.txt` (git is in the .NET SDK image; `.git` is no longer in `.dockerignore`). `ComputeBuildStamp()` in `Program.cs` reads the ISO timestamp and formats it as `v{YY}.{MM}.{DD}.{HHMM}` (Africa/Johannesburg / SAST). Shown in the SPA header next to "VMentory" and in `/health` + `/api/state` as `build`. Falls back to `"dev"` outside Docker.

11. **`docker cp` changes file ownership.** `docker cp` runs as root — copying a file into a container path on a volume sets the owner to `root`. The VMentory container runs as uid 10001 (`vmentory`). SQLite in WAL mode requires write access to the main DB file and the `-shm`/`-wal` sidecar files, even for reads. After any manual `docker cp` into the data volume, always run:
    ```bash
    chown 10001:10001 /var/lib/docker/volumes/<app-vol>/_data/vmentory.db*
    docker restart <container>
    ```
    Without the `chown`, the container will fail to open the DB on restart.

13. **Proxmox auth header must use `TryAddWithoutValidation`.** PVE's token scheme is
    `Authorization: PVEAPIToken=user@realm!tokenid=uuid` — it uses `=` instead of the standard
    `scheme<space>credentials` separator and contains `!`. .NET's `HttpRequestHeaders.Add("Authorization", …)`
    **validates** the value and throws `FormatException` *before the request is sent*, which surfaced as a
    generic "Unexpected error during connect" and made every Proxmox host look unreachable even with a
    valid token + open port. `ProxmoxProvider.BuildClient` uses `TryAddWithoutValidation` to send it
    verbatim (verified live against vega14, 2026-06-18). Also: the stored token is the **bare**
    `user@realm!tokenid=uuid` (no `PVEAPIToken=` prefix — the provider adds it); a wrong realm/token-id
    gives HTTP **401 "Authentication failed!"** (vs **403** for a valid-but-unprivileged token).
    **Forthcoming (ENG-0012, planned):** the token will be **stored decomposed** (`user@realm` + `tokenid`
    as non-secret descriptor columns, the secret UUID in the vault) and **recomposed bare** at
    `BuildClient` — this gotcha is explicitly **preserved**, the wire format is unchanged. **Forthcoming
    (ENG-0011a, planned):** the merged `catch … when (401 || 403)` is split — **401 → `AUTH_REJECTED`
    (Unauthorized)**, **403 → `AUTH_INSUFFICIENT_PRIV` (Degraded)**.

12. **Admin password recovery.** If the admin password is unknown (e.g. auto-generated and the container was replaced before the startup log was captured): (1) Set `VMENTORY_ADMIN_PASSWORD` in the Coolify environment and restart — this re-seeds the admin account only if no admin exists yet; if the account already exists, the env var is ignored. (2) To force a reset, compute a new PBKDF2-SHA256 hash (`100000:{base64_salt}:{base64_hash}`) and write it directly to the `PasswordHash` column in the DB. The hash format is identical between Python `hashlib.pbkdf2_hmac('sha256', pw.encode(), salt, 100000, dklen=32)` and .NET `Rfc2898DeriveBytes.Pbkdf2(string, ...)` — both use UTF-8 password encoding. Copy the updated DB back to the volume, run `chown 10001:10001` on it (see gotcha #11), then restart the container.

14. **The Hyper-V provider does not work in the container — at all, and never has.** Not a config problem, not a credential problem, not a firewall problem. `Reachability.RunPowerShellAsync` (`Reachability.cs:169`) launches **`powershell.exe`**, the ICMP check (`:20`) launches **`ping.exe`**, `:52` ensures the **local** WinRM service and `:80-82` mutates **`WSMan:\localhost\Client\TrustedHosts`**, and both `Reachability.cs:113` and `Scanner.cs:216` run `Invoke-Command -ComputerName … -Authentication Negotiate` — the whole path assumes **the Core process is itself a domain-joined Windows WinRM client**, which was true of the Phase-1 single-exe and became false the moment slice (1) containerized it (ENG-0010). On Linux the only surviving check is the bare TCP probe at `Program.cs:517`, so **a perfectly healthy HV host presents as "port open, every scan failed."** This is the root cause of the ENG-0011 trigger, which was misread as a *logging* gap for six weeks. **Do not try to fix this by porting the PowerShell path** — `powershell.exe` is not coming to the image. **ENG-0013 replaces it** with SSH + PowerShell executed *on the host* via the shared `ISshExecutor`; `BuildRemoteWrapper`, `RunPowerShellAsync`, the WinRM ensure and the `TrustedHosts` mutation are all **deleted, not ported**. Until slice (7) lands, treat any HV host in the deployed dev instance as **expected-broken**, not as a bug to chase.
