# Docs session brief — 2026-06-18

Read this first before touching any docs. It covers everything shipped since the last docs pass.
Delete this file when the docs session is done.

---

## Slices landed (need full docs coverage)

### Slice 2 — Auth + RBAC (ENG-0008)
- Cookie-based session auth: `POST /api/auth/login` → sets `vmentory_session` (HttpOnly, Secure, SameSite=Strict, 12h sliding)
- PBKDF2-SHA256 password hashing (BCL-only): format `{iterations}:{base64_salt}:{base64_hash}`, 100k iterations
- Four roles: `Admin / VmOperator / BackupOperator / Viewer` (defined in `RbacCatalog.cs`)
- First-admin seed: `VMENTORY_ADMIN_USER` (default `admin`) + `VMENTORY_ADMIN_PASSWORD` env vars; auto-generates + prints password to stdout if `VMENTORY_ADMIN_PASSWORD` not set; always `MustChangePassword=true`
- New entities: `AppUserEntity`, `AuditEventEntity`
- New migration: `AddAuth`
- New API endpoints: `POST /api/auth/login`, `GET /api/auth/me`, `POST /api/auth/logout`, `POST /api/auth/change-password`
- Auth chokepoint middleware gates all `/api/*` (except `/api/auth/*`); password-rotation gate locks `MustChangePassword` users to `/api/auth/*` only

### Slice 3 — ISecretStore (ENG-0002)
- `ISecretStore` interface: `SetAsync / GetAsync / DeleteAsync`
- `AesGcmSecretStore` (Scoped): AES-256-GCM per-value encryption in SQLite. Each value encrypted with a unique 12-byte nonce; stored as `base64(cipher || 16-byte-tag)`
- `DekProvider` (Singleton): holds decrypted DEK in memory. On first use: generates DEK, wraps it with the KEK (AES-256-GCM), persists as a single `DekEntity` row. On restart: loads and unwraps with KEK.
- `EphemeralSecretStore` (Singleton): `ConcurrentDictionary` in-memory fallback when no KEK is set
- KEK env var: `VMENTORY_KEK` (base64 32-byte AES-256 key). If set + persist mode → `AesGcmSecretStore`. If absent → `EphemeralSecretStore`.
- New entities: `SecretEntity`, `DekEntity`
- New migration: `AddSecrets`
- Startup banner now shows `Secrets : {mode}` (either `AES-256-GCM (SQLite)` or `ephemeral — set VMENTORY_KEK...`)

---

## Infra / DevOps changes

### Auto-deploy (GitHub → Coolify)
- GitHub webhook on `AJ-dev-i60/VMentory` pointing to `https://coolify.edgestudios.co.za/webhooks/source/github/events/manual`
- Signed with `manual_webhook_secret_github` via HMAC-SHA256 (`X-Hub-Signature-256`)
- Fires on all pushes; Coolify filters by `git_branch = dev`
- `VMENTORY_ADMIN_PASSWORD=VMentory2026!` is set in the Coolify env for the dev app (so the seed gets a stable password on volume resets)

### Build stamp
- `Dockerfile` build stage: `git log -1 --format=%cI HEAD > /app/build-stamp.txt` (`.git` is no longer excluded from `.dockerignore`)
- `ComputeBuildStamp()` in `Program.cs` reads the ISO timestamp → formats `v{YY}.{MM}.{DD}.{HHMM}` (Africa/Johannesburg / SAST)
- Shown in SPA header next to "VMentory" (small, low-opacity span), and in `/health` + `/api/state` as `build`
- Falls back to `"dev"` when running outside Docker

### CI workflow
- `.github/workflows/ci.yml`: triggers on push/PR to `dev` and `main`; runs `dotnet restore` + `dotnet build VMentory.sln -c Release`

### Deleted / removed
- `Updater.cs` deleted (dead code — Windows exe auto-updater, irrelevant in container mode)
- `--no-update` CLI arg still accepted (silently ignored) for backward compatibility

---

## Operational notes (for runbook / gotchas section)

### docker cp ownership
`docker cp` runs as root. Copying a file into a container path on a volume changes file ownership to root. The VMentory container runs as uid 10001 (`vmentory`). SQLite in WAL mode requires write access to the main DB and the `-shm`/`-wal` sidecar files — even for reads. After any manual `docker cp` into the data volume, always run:
```bash
chown 10001:10001 /var/lib/docker/volumes/<app-vol>/_data/vmentory.db*
docker restart <container>
```

### Admin password recovery
If the admin password is unknown (e.g. auto-generated and container was replaced before logs were read):
1. Set `VMENTORY_ADMIN_PASSWORD` in Coolify env
2. Copy the DB, update the `PasswordHash` column using PBKDF2-SHA256 (`100000:{base64_salt}:{base64_hash}`, Python `hashlib.pbkdf2_hmac('sha256', pw.encode(), salt, 100000, dklen=32)`), copy back, `chown 10001:10001`, restart
3. The hash format is identical between Python `hashlib` and .NET `Rfc2898DeriveBytes.Pbkdf2(string, ...)` (both use UTF-8 password encoding)

---

## Files that changed (for PROGRESS.md / file map updates)

| File | Change |
|---|---|
| `VMentory.Core/Secrets/ISecretStore.cs` | NEW |
| `VMentory.Core/Secrets/AesGcmSecretStore.cs` | NEW |
| `VMentory.Core/Secrets/EphemeralSecretStore.cs` | NEW |
| `VMentory.Core/Secrets/DekProvider.cs` | NEW |
| `VMentory.Core/Persistence/AppUserEntity.cs` | NEW |
| `VMentory.Core/Persistence/AuditEventEntity.cs` | NEW |
| `VMentory.Core/Persistence/SecretEntity.cs` | NEW |
| `VMentory.Core/Persistence/DekEntity.cs` | NEW |
| `VMentory.Core/Persistence/EfUserStore.cs` | NEW |
| `VMentory.Core/Persistence/IUserStore.cs` | NEW |
| `VMentory.Core/Auth/AppRole.cs` | NEW |
| `VMentory.Core/Auth/PasswordHasher.cs` | NEW |
| `VMentory.Core/Migrations/AddAuth` | NEW migration |
| `VMentory.Core/Migrations/AddSecrets` | NEW migration |
| `VMentory.Core/Persistence/VMentoryDbContext.cs` | Added Users, AuditEvents, Secrets, Deks DbSets + model config |
| `RbacCatalog.cs` | NEW |
| `Program.cs` | Major: auth routes, ISecretStore wiring, build stamp, seed logic, removed Updater calls |
| `Dockerfile` | Added build stamp step; `.git` now included in build context |
| `.dockerignore` | Removed `.git` exclusion |
| `wwwroot/index.html` | Login wall, change-password wall, RBAC-aware UI, build stamp in header |
| `Updater.cs` | DELETED |
| `.github/workflows/ci.yml` | NEW |
| `README.md` | Full rewrite (Phase 2 container-first) |
| `CLAUDE.md` | Updated file map, env contract, API surface, gotchas |
