# VMentory

Single-exe Windows tool that inventories Hyper-V hosts over WinRM and serves a local web dashboard on 127.0.0.1.

- **Repo**: https://github.com/AJ-dev-i60/VMentory
- **Stack**: ASP.NET Core 8 minimal API · vanilla JS SPA (no framework) · PowerShell subprocess for WinRM · custom Canvas donut charts

---

## File map

| File | Purpose |
|---|---|
| `Program.cs` | Entry point: API routes, config, console loop, `ErrorLogger` |
| `Models.cs` | All data types: `Host`, `Vm`, `Vhd`, `Volume`, `Credentials`, enums |
| `Store.cs` | Thread-safe in-memory state (`ConcurrentDictionary`), diff, totals |
| `Scanner.cs` | WinRM full-inventory scan + quick-connect via PowerShell `Invoke-Command` |
| `Reachability.cs` | Ping / TCP / WinRM-auth checks + `RunPowerShellAsync` helper |
| `Poller.cs` | `BackgroundService`: reachability re-check every 30 s |
| `EventHub.cs` | SSE broadcast via `System.Threading.Channels` |
| `MockData.cs` | 5 fake hosts for `--mock` mode |
| `Exporter.cs` | CSV zip + JSON export |
| `Updater.cs` | GitHub Releases auto-update: apply-on-launch + background download |
| `wwwroot/index.html` | Entire SPA (CSS + JS inline, ~1600 lines) |
| `HyperInventory.csproj` | SDK Web project; `AssemblyName=VMentory`; `Version` defaults to `1.0.0` |
| `build.ps1` | Release build: `dotnet publish` win-x64 single-file → `dist\VMentory.exe` |
| `.github/workflows/release.yml` | CI: push `v*` tag → build → GitHub Release with `VMentory.exe` asset |

---

## Dev commands

```powershell
dotnet run -- --mock                # dev mode: 5 fake hosts, no real WinRM
dotnet run -- --mock --no-update    # same, skip GitHub update check
dotnet build                        # compile check
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

2. **`HyperInventory.Host` must be fully qualified** in `Program.cs` — ambiguous with `Microsoft.Extensions.Hosting.Host`. Two locations: snapshot lambda and `AddHost` call.

3. **HTML served via `Assembly.GetManifestResourceStream()`** — not `StaticFiles` or `ManifestEmbeddedFileProvider` (that requires a NuGet package unavailable offline). See `LoadEmbeddedHtml()` in `Program.cs` and `<EmbeddedResource>` in the csproj.

4. **`ErrorLogger` uses `FileShare.ReadWrite`** — allows two dev instances to share the log without crashing.

5. **Publish flags are CLI-only** — adding `-r win-x64` to the csproj breaks offline `dotnet restore`. Always pass via `build.ps1` or explicit `dotnet publish` flags.

6. **Auto-update skips dev mode** — `Updater` checks `Path.GetFileName(ProcessPath) == "VMentory.exe"`; `dotnet run` never triggers update logic.
