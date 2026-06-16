# VMentory

> **Phase 2 in planning.** This document describes the shipping Phase-1 app (below). Phase 2 turns
> VMentory into a **container-based, single-operator, multi-platform (Hyper-V + Proxmox) platform**
> with on-device agents, delivering **four pillars on one shared foundation** —
> **Observe → Migrate → Deploy → Backup** (ENG-0006). The shared foundation is a .NET-native agent
> (gRPC/mTLS, ENG-0001/0004), `ISecretStore` (ENG-0002), a private-CA PKI (ENG-0005), persistence,
> and a general operations engine. Start at
> [`docs/phase2/PROGRESS.md`](docs/phase2/PROGRESS.md) → `ARCHITECTURE.md` / `ROADMAP.md`, and
> `docs/engineering/REGISTER.md` for the decision register. Agents live in `.claude/agents/`.

Single-exe Windows tool that inventories Hyper-V hosts over WinRM and serves a local web dashboard on 127.0.0.1.

- **Repo**: https://github.com/AJ-dev-i60/VMentory
- **Stack**: ASP.NET Core 8 minimal API · vanilla JS SPA (no framework) · PowerShell subprocess for WinRM · custom Canvas donut charts

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

| `Program.cs` | Entry point: API routes, config, console loop, `ErrorLogger` |
| `VMentory.Core/Models.cs` | All data types: `Host` (incl. `Platform` discriminator), `Vm`, `Vhd`, `Volume`, `Credentials`, enums (in `VMentory.Core`) |
| `VMentory.Core/IVirtualizationProvider.cs` | Provider abstraction (`Platform`, `Capabilities`, `QuickConnectAsync`, `ScanAsync`) — the Core↔platform seam |
| `VMentory.Core/ProviderCapability.cs` | `[Flags]` capability enum + `ProviderCapabilities` (gates UI + ops engine; HV mgmt verbs allowed per ENG-0007) |
| `VMentory.Core/PlatformKind.cs` | `HyperV` / `Proxmox` discriminator |
| `HyperVProvider.cs` | `IVirtualizationProvider` for Hyper-V; wraps `Scanner`/`Reachability`, resolves creds from `Store`/`AppConfig`. Inventory reads (quick-connect, scan) now flow through this seam |
| `Store.cs` | Thread-safe in-memory state (`ConcurrentDictionary`), diff, totals |
| `Scanner.cs` | WinRM full-inventory scan + quick-connect via PowerShell `Invoke-Command` |
| `Reachability.cs` | Ping / TCP / WinRM-auth checks + `RunPowerShellAsync` helper |
| `Poller.cs` | `BackgroundService`: reachability re-check every 30 s |
| `EventHub.cs` | SSE broadcast via `System.Threading.Channels` |
| `MockData.cs` | 5 fake hosts for `--mock` mode |
| `Exporter.cs` | CSV zip + JSON export |
| `Updater.cs` | GitHub Releases auto-update: apply-on-launch + background download |
| `wwwroot/index.html` | Entire SPA (CSS + JS inline, ~1600 lines) |
| `VMentory.Web.csproj` | SDK Web project (the exe); `AssemblyName=VMentory`; `Version` defaults to `1.0.0`; references `VMentory.Core` |
| `VMentory.Core/VMentory.Core.csproj` | Classlib SDK project; domain model |
| `VMentory.sln` | Solution tying `VMentory.Web` + `VMentory.Core` together |
| `build.ps1` | Release build: `dotnet publish` win-x64 single-file → `dist\VMentory.exe` |
| `.github/workflows/release.yml` | CI: push `v*` tag → build → GitHub Release with `VMentory.exe` asset |

---

## Dev commands

```powershell
dotnet run --project VMentory.Web -- --mock              # dev mode: 5 fake hosts, no real WinRM
dotnet run --project VMentory.Web -- --mock --no-update  # same, skip GitHub update check
dotnet build VMentory.sln                                # compile check (whole solution)
.\build.ps1                         # release exe → dist\VMentory.exe
.\build.ps1 -Version 1.2.0          # embed specific version number
```

## Release workflow

```powershell
git add -p && git commit -m "feat: ..."
git tag v1.2.0
git push && git push --tags         # Actions builds and publishes the release automatically
```

---

## API surface

| Method | Path | Description |
|---|---|---|
| GET | `/api/state` | Full snapshot: hosts, totals, diff, `credentialsSet`, `mockMode` |
| POST | `/api/credentials` | Set global WinRM credentials |
| POST | `/api/hosts` | Add hosts (DNS + reachability + quick connect) |
| DELETE | `/api/hosts/{id}` | Remove host |
| POST | `/api/scan` | Trigger full inventory (max 3 concurrent, fire-and-forget) |
| GET | `/api/events` | SSE stream — token via `?token=` query param |
| GET | `/api/export/json` | Download JSON export |
| GET | `/api/export/csv` | Download CSV zip |
| POST | `/api/quit` | Purge session data + shutdown |

All `/api/*` routes require `X-Session-Token` header or `?token=` query param.

---

## Non-obvious gotchas

1. **PowerShell scripts use `string.Format()`** — never raw string literals (`$"""..."""`). C# interpolation braces conflict with PowerShell `{}` blocks. See `Scanner.cs:BuildRemoteWrapper` and `Reachability.cs`.

2. **`VMentory.Core.Host` must be fully qualified** in `Program.cs` — ambiguous with `Microsoft.Extensions.Hosting.Host`. Three locations: the `hostsToCheck` list, the `AddHost` call, and the snapshot lambda.

3. **HTML served via `Assembly.GetManifestResourceStream()`** — not `StaticFiles` or `ManifestEmbeddedFileProvider` (that requires a NuGet package unavailable offline). See `LoadEmbeddedHtml()` in `Program.cs` and `<EmbeddedResource>` in the csproj.

4. **`ErrorLogger` uses `FileShare.ReadWrite`** — allows two dev instances to share the log without crashing.

5. **Publish flags are CLI-only** — adding `-r win-x64` to the csproj breaks offline `dotnet restore`. Always pass via `build.ps1` or explicit `dotnet publish` flags.

6. **Auto-update skips dev mode** — `Updater` checks `Path.GetFileName(ProcessPath) == "VMentory.exe"`; `dotnet run` never triggers update logic.
