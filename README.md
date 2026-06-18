# VMentory

A container-based, single-operator, multi-platform (Hyper-V + Proxmox) infrastructure management platform. Ships as a single Linux container with no per-host agent required on Proxmox nodes.

- **Repo**: https://github.com/AJ-dev-i60/VMentory
- **Dev instance**: https://vmentorydev.edgestudios.co.za
- **Stack**: ASP.NET Core 8 minimal API · vanilla JS SPA · EF Core + SQLite · AES-256-GCM secret store
- **Phase 2 docs**: [`docs/phase2/PROGRESS.md`](docs/phase2/PROGRESS.md) — current build state, slice order, all decisions

## Quick start (container)

```bash
docker run -d \
  -p 8443:8443 \
  -v vmentory-data:/data \
  -e VMENTORY_HTTP_ONLY=1 \
  -e VMENTORY_ADMIN_PASSWORD=changeme \
  -e VMENTORY_KEK=$(openssl rand -base64 32) \
  ghcr.io/aj-dev-i60/vmentory:latest
```

Open https://localhost:8443, sign in as `admin` / `changeme`, change the password on first login.

## Environment variables

| Variable | Default | Description |
|---|---|---|
| `VMENTORY_HTTP_ADDR` | `0.0.0.0` | Bind address |
| `VMENTORY_HTTP_PORT` | `8443` | Listen port |
| `VMENTORY_HTTP_ONLY` | — | Set to `1` for plain HTTP (behind a reverse proxy) |
| `VMENTORY_DB` | `/data/vmentory.db` | SQLite path (mount `/data` as a volume) |
| `VMENTORY_ADMIN_USER` | `admin` | First-admin username (seeded on first startup) |
| `VMENTORY_ADMIN_PASSWORD` | *(auto-generated)* | First-admin password; auto-generated + printed to logs if not set |
| `VMENTORY_KEK` | — | Base64 32-byte key; enables AES-256-GCM credential encryption (generate: `openssl rand -base64 32`) |
| `VMENTORY_TLS_PFX` | — | Path to operator PFX cert (+ `VMENTORY_TLS_PFX_PASSWORD`) |
| `VMENTORY_TLS_CERT_PEM` | — | Path to operator cert PEM (+ `VMENTORY_TLS_KEY_PEM`) |

## Dev commands

```powershell
dotnet build VMentory.sln
dotnet bin\Debug\net8.0\VMentory.dll --mock    # 5 fake hosts, no real WinRM
.\build.ps1                                    # → dist\VMentory.exe (Windows exe)
```

## Hyper-V host requirements

Enable WinRM on each target Hyper-V host:
```powershell
Enable-PSRemoting -Force
```

For non-domain clients, add the host to TrustedHosts:
```powershell
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "hv-host-01" -Force
```

WinRM uses TCP 5985 (HTTP) or 5986 (HTTPS). VMentory uses **Negotiate** auth (Kerberos / NTLM).

## Security

- Cookie-based session auth (HttpOnly, Secure, SameSite=Strict, 12h sliding)
- PBKDF2-SHA256 password hashing (BCL-only, 100k iterations, work-factor stored in hash)
- AES-256-GCM envelope encryption for recoverable secrets (`VMENTORY_KEK` → per-DB DEK)
- Fixed RBAC roles: Admin / VmOperator / BackupOperator / Viewer
- Single audited authz chokepoint before any write verb
- No secrets ever plaintext at rest; no secrets in the image or this repo
