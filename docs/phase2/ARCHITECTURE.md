# VMentory Phase 2 — Target Architecture

> Status: **draft** · Owner: codebase side · Last decisions locked: 2026-06-16 (ENG-0001..0010;
> foundation re-baselined and approved — ENG-0009/0010, amendments to 0001/0003/0004/0005)
>
> Phase 1 = single-exe, Windows-only, read-only, ephemeral Hyper-V inventory.
> Phase 2 = a **hosted, web-first, container-based, single-operator, multi-platform (Hyper-V +
> Proxmox) platform** (ENG-0010), delivering **four product pillars on one shared foundation**
> (ENG-0006), **weighted and sequenced toward a Proxmox-first North Star** (ENG-0007). **Proxmox is
> driven by its native REST API + a constrained SSH key, with no node agent (ENG-0009);** an on-device
> agent runs only on the retiring **Hyper-V** hosts as the migration source.

This document is the spine. The documentation agent expands the per-component specs
under `docs/phase2/specs/`; the UI design agent works the visual side under `design/`. The
engineering decisions these reflect live in [`docs/engineering/`](../engineering/REGISTER.md) —
cite the `ENG-NNNN` next to any decision you rely on.

---

## The product: four pillars on a shared foundation (ENG-0006), Proxmox-first (ENG-0007)

VMentory is **not** "dashboard + management + one-way migration." It is a single-operator platform
that delivers **four pillars, each of standalone value**, on one shared foundation (the agent,
provider model, secret store, PKI, persistence, and a general operations engine).

**North Star (ENG-0007):** the organization is moving **entirely off Hyper-V onto Proxmox.** That
makes VMentory v2 a **Proxmox-first management super-tool**, not a neutral two-platform peer product.
Hyper-V is the **declining, transitional** platform — its primary job in v2 is to be the thing
migrated **out of** — kept operable through the transition by **light management** (start / stop /
reconfigure), never brought to full parity.

The four pillars (ENG-0006) **stand as defined**, but they are **not equal**. ENG-0007 **refines and
sequences** them by *effort weighting* under the Proxmox-first lens — it does **not** delete pillars
or supersede ENG-0006. The practical ordering of work is
**Observe (plant) → Proxmox management/Deploy → HV→PVE migration**: get persistent visibility, then
get the estate *onto* and *operating well on* Proxmox, with migration as the bridge.

| Pillar | What it is | Weight / status (ENG-0007) |
|---|---|---|
| 1. **Observe (plant)** | Resource/load dashboard: VM distribution, host utilization, capacity planning. **Not passive** — feeds *placement recommendations* consumed by Migrate and Deploy. Being **"planted"** onto persistent, multi-platform storage — this is **foundation work, not a fifth pillar**. | **Weighted first.** Phase-1 root; planted = **v2.0** |
| 2. **Migrate** | Wizard-driven **HV→PVE** (the priority direction — proven by the [`migrate-vm`](../../migrate-vm/SKILL.md) skill). Handles Windows checkpoint resolution and live-vs-shutdown by **operator choice**. **Bidirectional HV↔PVE remains the eventual end state**, but **PVE→HV reverse is deferred out of release 1** (ENG-0007). | **HV→PVE = 2.3**; PVE→HV later |
| 3. **Deploy** | VM provisioning **on Proxmox first**: ISO library + golden-image/template repository, creation wizard, target-hypervisor choice, **guest customization** (IP DHCP/static auto-config, post-deploy app install e.g. Atera with operator-supplied package). | Weighted alongside Proxmox management |
| 4. **Backup/Restore** | Backup + restore to **either** platform, reports, restore-testability, dedup, storage management, off-site replication. | **Out of release 1** (ENG-0007); buy-vs-build deferred |

**Hyper-V scope = light management (ENG-0007).** Beyond Observe, the Hyper-V provider gains **basic
start / stop / reconfigure** so the operator runs the whole estate from one tool while HV winds down.
This is deliberately **not source-only** (which would strand the operator on Hyper-V Manager for the
live production estate) and **not full parity** (which would be wasted investment in a platform being
retired). The discipline is **"light, not parity"**: no Deploy, no Backup, no advanced management on
Hyper-V.

**PVE→HV is a distinct later effort** and is **out of release 1** (ENG-0007) — not "the same wizard
reversed." It needs reverse driver handling (strip virtio, inject Hyper-V drivers), qcow2→VHDX
conversion, and Gen2/UEFI handling (ENG-0006).

**Incremental release cadence (ENG-0007).** There is **no big-bang v2**. Each milestone (2.0→2.4)
ships as its own usable release: **v2.0 = planted Observe** (persistent, multi-platform dashboard),
then Proxmox management/Deploy, then HV→PVE migration. The release model and per-milestone cut lines
live in [ROADMAP.md](ROADMAP.md).

### Shared subsystems the four-pillar vision requires (ENG-0006)

Pillars 3–4 add subsystems the original architecture lacked. Note them now so the foundation work
leaves room:

1. **Storage / repository layer** — ISOs, golden images/templates, **and** backup repositories
   (+ dedup). Underpins Deploy and Backup. *Biggest new gap; placement undecided — see Open questions.*
2. **Guest-customization layer** — cloud-init / sysprep / unattend, network (DHCP/static),
   post-deploy app install. **Shared by Deploy and Migrate** (the skill's netplan fix is its embryo) —
   build once, used by both.
3. **Scheduling** — backups are recurring; the operations engine needs a scheduler, not only
   on-demand jobs.
4. **Data movement at scale** — backup/restore/replication move large data (dedup, incrementals,
   offsite-over-WAN) — an axis migration alone did not demand.

The original "migration job engine" therefore generalizes into a **general operations engine**:
migrate, deploy, backup, and restore are all persisted, resumable, dry-runnable jobs on the same
machinery (§4).

---

## Deployment model (ENG-0010) — containerized, web-first, "install nothing"

VMentory v2 is a **hosted web service in a single Linux container**, not a desktop app. This is the
tenet the whole foundation is built around (ENG-0010), and it directly replaces the Phase-1
loopback/desktop bootstrap.

> **Foundation slices (1)–(3) built and DEPLOYED (2026-06-17/18).** The hosted-service bootstrap
> below is **live on `dev`** at <https://vmentorydev.edgestudios.co.za> (reverse-proxy mode: Traefik
> terminates TLS, app runs `VMENTORY_HTTP_ONLY=1` on HTTP 8443, `/data` named volume).
> [`Program.cs`](../../Program.cs) binds `0.0.0.0:{port}` via `ConfigureKestrel` and terminates HTTPS
> itself; [`TlsSetup.cs`](../../TlsSetup.cs) resolves the cert (operator PFX → PEM → self-signed,
> real-mode cached / mock ephemeral, nothing baked in); a [`Dockerfile`](../../Dockerfile) builds a
> non-root (uid 10001) Linux image (app + openssh-client, no `qemu`/`qm`) with DB + cert on a `/data`
> volume. **Login + RBAC (slice 2) is live:** the session token has been retired; cookie-based auth
> with fixed roles (Admin/VmOperator/BackupOperator/Viewer) gates all `/api/*`. **`ISecretStore`
> (slice 3) is live:** `AesGcmSecretStore` encrypts credentials in SQLite when `VMENTORY_KEK` is set.
> **Still pending:** `/api/quit` desktop route retirement; `Updater.cs` image-tag re-scope; login UI
> awaiting design. The env contract and cert precedence below match the shipped code.

- **The user installs nothing locally — there are exactly two installs.** (a) Stand up the **Core
  container** (`docker run` / compose); (b) install the **Hyper-V agent** on the *retiring* Windows HV
  hosts, and only when migration is in scope. **Proxmox needs no install at all** — it is onboarded by
  pasting a scoped API token + a constrained SSH key (ENG-0009). Everything else is reached *from* the
  container.
- **The container hosts all the tools.** The image carries the .NET app + an SSH client; it does **not**
  bake in `qemu`/`qm`/`virt-v2v` — those run **on the Proxmox node** and are reached over SSH (ENG-0009).
- **Network bind:** Core binds **`VMENTORY_HTTP_ADDR` (default `0.0.0.0`) : `VMENTORY_HTTP_PORT`**
  (env-driven) via Kestrel `ConfigureKestrel`, replacing the Phase-1 hardcoded `127.0.0.1:{random}`
  loopback + `FindFreePort()`. The random-port desktop discovery — and the `OpenBrowser()` / R-key
  reopen affordances — are **dropped**. The console Q-to-quit loop now runs **only when
  `!Console.IsInputRedirected`** (a container has no TTY, so it is skipped; the operator stops the
  service with SIGTERM → ASP.NET graceful shutdown).
- **TLS posture: Core-terminated HTTPS.** Kestrel serves HTTPS **directly** via `listen.UseHttps(cert)`
  — the **decided default for release 1** (ENG-0010). Cert resolution precedence
  ([`TlsSetup.cs`](../../TlsSetup.cs)) is **operator PFX (`VMENTORY_TLS_PFX` +
  `VMENTORY_TLS_PFX_PASSWORD`) → operator PEM (`VMENTORY_TLS_CERT_PEM` + `VMENTORY_TLS_KEY_PEM`) →
  self-signed fallback** (first-run, warn; cached in the data dir in real mode for a stable identity +
  one-time browser warning; **mock mode is ephemeral, writes nothing**). **`VMENTORY_HTTP_ONLY=1`**
  disables TLS for a TLS-terminating reverse proxy (nginx/Traefik) or local dev — a **documented future
  seam, NOT required for release 1**. This dashboard TLS is a **distinct trust domain** from the agent
  mTLS PKI (ENG-0005 / §5) — do not conflate the two certs.
- **Runtime injection, nothing baked in:** the envelope-encryption **KEK** (ENG-0002) **and** the
  dashboard **TLS cert/key** are injected at runtime (container secret / `LoadCredential` / env /
  mounted volume / operator-passphrase). The image bakes in **no secret**; the DB holds only
  KEK-wrapped secrets.
- **Persistence:** SQLite + the cached self-signed cert live on a **mounted `/data` volume** via
  `VMENTORY_DB` / `DataDir` (already wired); operator-supplied TLS material + config mount alongside.
  Postgres stays the opt-in multi-instance path.
- **Container hardening:** the image runs as a **non-root** user (uid 10001); `/data` is the only
  writable volume; it carries the .NET app + `openssh-client` (ENG-0009) and **no `qemu`/`qm`**;
  `EXPOSE 8443`. The Windows-only `requireAdministrator` manifest is conditioned off the Linux publish.
  **Constraint (learned in deploy):** a non-root container can write **only** to mounted volumes — any
  app file write (e.g. `errors.log`) must resolve to a writable path. `ErrorLogger` writes to
  `VMENTORY_LOG` → the `VMENTORY_DB` `/data` dir → app base and is **fail-safe** (degrades to a no-op;
  logging never crashes startup) after a uid-10001 write into root-owned `/app` crash-looped the first
  deploy (exit 139, fixed in `ca196f4`).
- **Liveness:** an unauthenticated **`GET /health`** probe (added in slice (1)) lets the orchestrator
  (Coolify/Traefik) health-check the container without a token.
- **Auth swap (done — slice (2), 2026-06-18):** the single session token is **retired**; login + RBAC
  (ENG-0008) is live. Cookie-based session auth (`vmentory_session`, HttpOnly Secure SameSite=Strict,
  12h sliding) with fixed built-in roles (Admin / VmOperator / BackupOperator / Viewer) over the
  `ProviderCapability`+`ConsolePermission` catalog. First-admin seed via `VMENTORY_ADMIN_USER`/
  `VMENTORY_ADMIN_PASSWORD`; `/api/quit` is still present (desktop affordance, pending retirement).
- **Inbound surface:** Core initiates **all** Proxmox connections outbound (REST 8006 + SSH 22,
  ENG-0009); nodes never connect in. The first containerized releases therefore expose **only the web
  UI port**. The agent gRPC/mTLS listener is **Hyper-V-only** and arrives later with the migration slice.

The foundation slice order this dictates lives in [ROADMAP.md](ROADMAP.md): **(1) containerized Core +
hosted bootstrap → (2) login + RBAC → (3) `ISecretStore` → (4) Proxmox API read → (5) Proxmox write
verbs → (6) Hyper-V agent + private CA → (7) HV→PVE migration**. The agent/CA foundation is **demoted
off the front of this list** and scoped to Hyper-V (ENG-0009) — it is no longer built first.

---

## Locked decisions

Each cites the engineering decision (`ENG-NNNN`) that produced it; see
[`docs/engineering/REGISTER.md`](../engineering/REGISTER.md). Don't silently contradict a **Decided**
item — supersede it through the engineering protocol first.

| # | Decision | Choice | Source |
|---|---|---|---|
| 1 | How the hosted tool reaches **Hyper-V** | **Agent installed on the Windows host**, reimplemented **natively in .NET** (no winrun.py / Python in the product). Transport = **gRPC over HTTP/2 + mTLS**. The agent drives the HV migration source from day one — there is **no winrun.py fallback**. **Scope (amended 2026-06-16, ENG-0009):** this agent is for **Hyper-V only**, and is **demoted off the 2.0 critical path** — sequenced with the migration slice, not built first. | ENG-0001, ENG-0004, ENG-0009 |
| 2 | Secret model | **`ISecretStore` provider abstraction** + v1 **app-native envelope encryption** (AES-256-GCM in SQLite, DEK wrapped by a **runtime-injected KEK**); secrets **never plaintext at rest**. Binding principle: **prefer scoped keys/tokens over passwords**. Vault/OpenBao + Azure KV are optional providers later. | ENG-0002 |
| 3 | Migration tool orchestration | **Orchestrate proven tools.** The validated path is **`qm importdisk` on the PVE node** (reads VHDX off a CIFS mount, converts VHDX→raw) + targeted guest fixes. **virt-v2v is OPTIONAL** (Windows virtio injection), **not the baseline**. | ENG-0001, ENG-0006 |
| 4 | State model | **Hybrid** — registry/jobs/history persisted (SQLite/EF Core); secrets via `ISecretStore` (decision #2). | ENG-0002 |
| 5 | Agent install / onboarding | **Manual / org-managed only** (MSI / GPO / SCCM / Intune) + a short-lived **enrollment token**. **No remote push-install**; VMentory never receives an admin credential. | ENG-0003 |
| 6 | Agent runtime | **NativeAOT single binary**, Windows Service / systemd unit; **constrained verb executor** (fixed versioned verb set, never arbitrary PowerShell/RCE); **gMSA-preferred** identity (local-account fallback); **self-update over its own mTLS channel** with watchdog rollback. | ENG-0004 |
| 7 | mTLS PKI | **Private CA inside Core** (external-CA seam later); **root + intermediate**; **short-lived agent certs + auto-renew** over the mTLS channel; revocation = stop-renew + a registry allow/deny list (**no CRL/OCSP**). CA key in `ISecretStore`. | ENG-0005 |
| 8 | Product scope | **Four pillars on one shared foundation**; conceptually **Observe → Migrate → Deploy → Backup**; **bidirectional migration end-state, HV→PVE first**; **backup buy-vs-build deferred**. | ENG-0006 |
| 9 | Product strategy & release scope | **Proxmox-first North Star** (retire Hyper-V → Proxmox). **Incremental release cadence** — each milestone (2.0→2.4) is its own usable release; **v2.0 = planted Observe**. Pillars **weighted/sequenced by effort**: **Observe (plant) → Proxmox management/Deploy → HV→PVE migration**. **Hyper-V = light management** (start/stop/reconfigure), not source-only, not full parity → **the `IVirtualizationProvider` capability model must allow management verbs on the HV provider**. **PVE→HV reverse migration and Backup/Restore are out of release 1.** Refines/sequences ENG-0006, does not supersede it. | ENG-0007 |
| 10 | How the hosted tool reaches **Proxmox** | **Native PVE REST API as the primary control plane** (orchestration, lifecycle, provisioning, stats/`rrddata`, native PVE↔PVE migration, UPID task progress) + a **constrained, forced-command SSH key** for the disk-import / conversion / on-node guest-edit residue (`qm importdisk`, `qemu-img`, optional `virt-v2v`, mount-and-edit guest fixes). **No agent on Proxmox nodes** — onboard = scoped API token + SSH key, both in `ISecretStore`, both revocable. This **demotes/scopes the agent foundation (ENG-0001/0003/0004/0005) to Hyper-V** and off the 2.0 critical path. | ENG-0009 |
| 11 | Console authn/authz | **Fixed built-in roles** (Admin / VM-operator / Backup-operator / Viewer) over an **internal pillar×verb permission catalog** reusing `ProviderCapability` + a small console-permission set; **local accounts now** (hashed, KEK-wrapped), **OIDC seam later**; a **single audited authorization chokepoint** in Web/API **and** the operations engine that **must exist before any write verb**. Firm + early: login in the deployment slice, roles before the first write verbs. | ENG-0008 |
| 12 | Core deployment & runtime | **Containerized Core** (single Linux image: app + SSH client, no `qemu`/`qm`). Bind **`0.0.0.0:{configurable port}`**; **Core-terminated HTTPS** (Kestrel, self-signed/provided cert) as the release-1 default, reverse-proxy a documented future seam. **KEK + TLS material injected at runtime**, nothing baked in. SQLite on a mounted volume. **Session-token/loopback bootstrap replaced by login + RBAC.** | ENG-0010 |

These pin the rest of the design. If one changes, supersede it in `docs/engineering/` and revisit
this file first.

---

## Topology

```
                         ┌─────────────────────────────────────────────────┐
                         │  VMentory Core  (Linux container — ENG-0010)     │
                         │  0.0.0.0:{cfg} · Core-terminated HTTPS (Kestrel) │
   browser ──https──▶    │  ┌─────────┐  ┌──────────┐  ┌──────────────────┐ │
   (login + RBAC,        │  │ Web/API │  │ Provider │  │ Operations engine│ │
    ENG-0008/0010)       │  │  + SSE  │  │ registry │  │ (migrate/deploy/ │ │
                         │  └─────────┘  └────┬─────┘  │  backup/restore) │ │
                         │  ┌───────────────┐ │        └────────┬─────────┘ │
                         │  │ ISecretStore  │ │ ┌──────────────┐│           │
                         │  │ + internal CA │ │ │ storage/repo ││ scheduler │
                         │  │ (HV-only PKI) │ │ │ (ISO/image/  ││           │
                         │  └───────────────┘ │ │  backup) ⌁   ││           │
                         │   persistence       │ └──────────────┘│           │
                         │   (SQLite/Postgres) │                 │           │
                         └───────────┬─────────┴────────┬────────┼──────────┘
              PRIMARY: REST(8006) + SSH(22) outbound    │        │
              ┌──────────────────────────┘              │        │
              │  (Proxmox — no agent, ENG-0009)         │        │
              ▼                                          ▼        ▼
   ┌────────────────────────┐         gRPC/HTTP2 + mTLS  │  (HV migration slice only)
   │ Proxmox VE node(s)     │         ┌─────────────────┘
   │ API token (scoped)     │         ▼
   │ + constrained SSH key  │   ┌──────────────────────────────┐
   │ qm importdisk/qemu-img │   │ VMentory Agent (HV ONLY)      │
   │ / (virt-v2v optional)  │   │ NativeAOT service on the      │
   │ NATIVE PVE↔PVE migrate │   │ retiring Hyper-V host;gMSA/   │
   └────────────────────────┘   │ local; constrained verb exec; │
   (⌁ storage/repo placement     │ inventory→light-mgmt→         │
    undecided — Open questions)  │ migration-source verbs        │
                                 │ (demoted off 2.0 — ENG-0009)  │
                                 └──────────────────────────────┘
```

- **Core** is the only thing the user talks to (a hosted web console over HTTPS — ENG-0010), and the only thing that initiates connections out. It holds no platform-specific transport logic itself — it talks to *providers*.
- **Proxmox is the primary, agent-less path (ENG-0009).** Core drives it **outbound** over the **native PVE REST API** (scoped token — orchestration, lifecycle, provisioning, stats/`rrddata`, **native PVE↔PVE migration**, UPID task progress) plus a **constrained, forced-command SSH key** for the residue the API can't reach (`qm importdisk`, `qemu-img`, optional `virt-v2v`, on-node mount-and-edit guest fixes). **Nothing is installed on the node** — onboarding is a token + a key, both in `ISecretStore` and both revocable. (An optional conversion-host can come later if we want conversion off the PVE node — still open.)
- **Hyper-V is reached through a NativeAOT agent that runs *on* the host (ENG-0001/0003/0004), scoped to the migration source.** It relocates today's `Scanner.cs` / `Reachability.cs` logic — reimplemented **natively in .NET** — to where the Hyper-V module already works, eliminating Linux→WinRM auth pain, and replacing a stored Windows domain password with a scoped, revocable **mTLS client cert** (ENG-0001). The agent is a **constrained verb executor**, never a remote shell (ENG-0004). Its verb catalog spans **inventory, light management (start/stop/reconfigure), and migration-source** verbs (ENG-0007); it deliberately does **not** gain Deploy or Backup/Restore verbs — those are Proxmox-side, per "light, not parity."
- **The agent + its private-CA mTLS PKI are demoted and Hyper-V-scoped (ENG-0009).** They are **not** the front-loaded 2.0 foundation — Proxmox needs neither. They are sequenced **with the HV→PVE migration slice** (ROADMAP foundation slice 6). Because there is still no winrun.py fallback, "the agent can drive guest control + the migration step graph" remains the **gate for 2.3**, but it is built then, not first. The dashboard TLS (ENG-0010) is a **separate trust domain** from this agent PKI (ENG-0005).
- **The only inbound listeners** are the web UI port (always) and — *if/when* Hyper-V agents exist — the agent gRPC/mTLS endpoint (HV migration slice only). Proxmox nodes never connect in.

---

## Component breakdown

### 1. VMentory Core
ASP.NET Core 8 (keep the stack — it's already cross-platform and containerizes cleanly). The single
`HyperInventory` project splits into the projects below. The **target** layout is five projects; the
2.0 foundation lands them incrementally.

| Project | Responsibility | Status |
|---|---|---|
| `VMentory.Core` | domain model, provider abstraction, operations engine, `ISecretStore`, internal CA, persistence | **Exists** — classlib, `namespace VMentory.Core`. Currently holds: `Models.cs`, `IVirtualizationProvider`/`ProviderCapability`/`PlatformKind` (slice 2), `Persistence/` (EF Core SQLite — `VMentoryDbContext`, host registry, snapshots, users, audit, secrets/DEK; slices 3/(2)/(3)), `Auth/` (`AppRole`, `ConsolePermission`, `PasswordHasher`; slice (2)), `Secrets/` (`ISecretStore`, `AesGcmSecretStore`, `EphemeralSecretStore`, `DekProvider`; slice (3)), three EF migrations (`Initial`/`AddAuth`/`AddSecrets`). Ops engine / CA deferred to later slices. |
| `VMentory.Web` | minimal API, SSE, auth, serves the SPA | **Exists** — the exe at repo root, `namespace VMentory.Web`, `AssemblyName=VMentory`, references Core. Hosts `HyperVProvider`, `RbacCatalog` (static role→capability mapping), `TlsSetup`, `Poller`, `Store`, `EventHub`, `Scanner`, `Reachability`, `Exporter`, and `MockData`. Cookie-based login/RBAC chokepoint middleware is in `Program.cs` (slice (2)). |
| `VMentory.Providers.HyperV` | talks to the Windows agent (gRPC/mTLS) | **Deferred** to the agent slice. `HyperVProvider` lives in `VMentory.Web` for now (it still wraps the in-proc `Scanner`/`Reachability`); it relocates here once the transport becomes the gRPC/mTLS agent client. |
| `VMentory.Providers.Proxmox` | PVE REST client + SSH executor | **Deferred** to 2.1. |
| `VMentory.Agent` | the NativeAOT on-device service — Windows (Hyper-V) and Linux host roles (separate deployable, ENG-0004) | **Deferred** to its own slice. |

> **Rename `HyperInventory` → `VMentory.*` — done (slice 1, commit `d57d89d`).** The namespace was a
> Phase-1 fossil. The solution is now `VMentory.sln` over `VMentory.Core` + `VMentory.Web`; the
> domain-vs-framework `Host` ambiguity is resolved project-wide by a `global using Host =
> VMentory.Core.Host;` alias (`GlobalUsings.cs`).

### 2. Provider abstraction
The pivot from "Hyper-V tool" to "platform" lives here.

The sketch below is the **illustrative target shape** — the full surface the interface grows into as
the management/migration/deploy/backup pillars land. It is **not** the current contract.

```csharp
// TARGET SHAPE (illustrative) — not what ships in 2.0.
public interface IVirtualizationProvider
{
    PlatformKind Platform { get; }                 // HyperV | Proxmox
    ProviderCapabilities Capabilities { get; }     // what this provider can actually do

    Task<HostInfo>        GetHostAsync(...);        // inventory parity with Phase 1
    Task<IReadOnlyList<VmInfo>> GetVmsAsync(...);
    Task<VmStats>         GetStatsAsync(...);       // live + time-series

    Task LifecycleAsync(VmRef vm, LifecycleOp op);  // start/stop/shutdown/snapshot...

    // migration primitives (capability-gated)
    Task<DiskExport> ExportDiskAsync(...);
    Task            ImportDiskAsync(...);
    Task<VmRef>     CreateVmShellAsync(VmSpec spec);
}
```

**What actually ships in 2.0 (slice 2, commit `2cb54fd`).** The real interface in
[`VMentory.Core/IVirtualizationProvider.cs`](../../VMentory.Core/IVirtualizationProvider.cs) is
deliberately **lean** — the read-only surface the planted-Observe milestone needs, plus the
capability handles:

```csharp
public interface IVirtualizationProvider
{
    PlatformKind Platform { get; }
    ProviderCapabilities Capabilities { get; }
    Task<(bool Ok, string Error)> QuickConnectAsync(Host host, CancellationToken ct = default);
    Task<(bool Ok, string Error)> ScanAsync(Host host, CancellationToken ct = default);
}
```

- **Implemented now:** `Platform`, `Capabilities`, `QuickConnectAsync`, `ScanAsync`. `HyperVProvider`
  advertises **`Inventory | LiveStats`** only and wraps the existing `Scanner`/`ReachabilityChecker`.
- **Deferred (2.2/2.3):** the `GetHostAsync`/`GetVmsAsync`/`GetStatsAsync`/`LifecycleAsync`/
  `ExportDiskAsync`/`ImportDiskAsync`/`CreateVmShellAsync` **verb methods** above. The capability
  **flags** for them already exist (see the capability model below), so the engine/UI can reason
  about them before the methods land; growing this single-consumer interface later is non-breaking.
- The richer ref/DTO surface (`HostInfo`/`VmInfo`/`VmRef`/etc.) is specced in
  [provider-abstraction.md](specs/provider-abstraction.md) as the target; 2.0 still returns the
  Phase-1 domain via the `Store` the provider mutates.

- Today's `Host`/`Vm`/`Vhd`/`Volume` ([Models.cs](../../VMentory.Core/Models.cs)) are already close to platform-neutral — they now live in the `VMentory.Core` domain and `Host` carries a `Platform` discriminator (defaults `HyperV`, slice 2). Still to do: drop Hyper-V-only assumptions (e.g. `Generation`, `IntegrationServices` become provider-specific extensions) when the domain is generalized for Proxmox.

#### Capability model — management verbs are capability-gated **per provider** (ENG-0007)

This is the part the 2.0 foundation depends on, so it is specified concretely here. **Proxmox is the
primary, full management target; Hyper-V advertises only light management** (start / stop /
reconfigure) per the ENG-0007 "light, not parity" rule. The abstraction must therefore let **each
provider declare which management verbs it supports**, and **every caller — the operations engine, the
API, and the UI — must honor those flags**. A capability model designed as "Hyper-V = read + export
only, Proxmox = read + write" would have to be reworked later; do **not** build it that way.

Shape: a `[Flags]` enum surfaced per provider via `ProviderCapabilities`, checked before any verb is
offered or dispatched. **This is now real code** — the enum and record below are mirrored verbatim in
[`VMentory.Core/ProviderCapability.cs`](../../VMentory.Core/ProviderCapability.cs) (slice 2); keep the
two in sync if either changes.

```csharp
[Flags]
public enum ProviderCapability
{
    None             = 0,

    // Observe (every provider has these — the planted foundation, §3)
    Inventory        = 1 << 0,   // hosts, VMs, disks, volumes
    LiveStats        = 1 << 1,   // current CPU/mem/IO
    HistoricalStats  = 1 << 2,   // time-series / rrddata

    // Light management (HV advertises at least Start|Stop|Reconfigure; PVE has all of these)
    Start            = 1 << 3,
    Stop             = 1 << 4,   // includes graceful shutdown + hard stop variants
    Reconfigure      = 1 << 5,   // vCPU/memory (+ optionally disks/NICs — scope pinned in 2.2)
    Snapshot         = 1 << 6,   // checkpoint / snapshot create-delete-revert
    Reset            = 1 << 7,

    // Migration primitives (capability-gated; not all providers do all of these)
    ExportDisk       = 1 << 8,   // HV provider: locate/expose flat VHDX
    ImportDisk       = 1 << 9,   // PVE provider: qm importdisk
    CreateVmShell    = 1 << 10,  // provision a target shell

    // Deploy / Backup (PVE-first; HV deliberately does NOT advertise these — ENG-0007)
    Provision        = 1 << 11,  // create-from-template / install-from-ISO
    Backup           = 1 << 12,
    Restore          = 1 << 13,
}

public sealed record ProviderCapabilities(ProviderCapability Verbs /* , version, notes ... */);
```

Indicative per-provider advertisement (exact sets pinned as each milestone lands). **As of 2.0,
`HyperVProvider` advertises only `Inventory | LiveStats`** — the management/migration rows below are
the *target* the flags reserve, flipped on as 2.2/2.3 wire the verb methods:

| Capability group | `HyperVProvider` | `ProxmoxProvider` |
|---|---|---|
| Observe (Inventory / LiveStats / HistoricalStats) | ✅ `Inventory \| LiveStats` now; HistoricalStats with persistence | ✅ |
| Light management (Start / Stop / Reconfigure) | ✅ **(ENG-0007 — HV is operable, not source-only)** | ✅ |
| Snapshot / Reset | ✅ (transition operability) | ✅ |
| Migration primitives (ExportDisk / ImportDisk / CreateVmShell) | ExportDisk (migrate-out source) | ImportDisk + CreateVmShell (migrate-in target) |
| Deploy (Provision) | ❌ **(light, not parity)** | ✅ |
| Backup / Restore | ❌ | ✅ (when pillar 4 lands; out of release 1) |

- **Capabilities gate the UI and the engine.** A provider advertises what it supports; the UI only
  renders verbs the target can do, and the operations engine refuses to dispatch a verb a provider
  doesn't advertise. Avoids "Migrate"/"Deploy" buttons that 500.
- **Hyper-V is not a read-only provider.** Per ENG-0007 it must advertise at least
  `Start | Stop | Reconfigure`. The light-management verbs ride the **same agent
  (constrained-verb executor, ENG-0004), provider, and operations machinery** built for Proxmox — the
  marginal cost is the verb wiring, not a new subsystem.
- **`Reconfigure` scope is still being pinned** (vCPU/memory only vs disks/NICs/checkpoints) — see
  the open sub-question in [ENG-0007](../engineering/discussions/0007-product-strategy-release-scope.md#open-sub-questions);
  resolved when 2.2 management verbs are specced.

### 3. Persistence (hybrid · ENG-0002) — the soil the Observe layer is "planted" into (ENG-0007)
Phase-1 Observe is **in-memory, single-use, ephemeral** ([Store.cs](../../Store.cs) is a
`ConcurrentDictionary`; [Program.cs](../../Program.cs)'s `/api/quit` purges on shutdown). **"Planting"
Observe (ENG-0007) means re-homing that read-only layer onto this persistent, multi-platform store** —
it is the **v2.0 release** and the foundation everything else builds on, not a new pillar. The hybrid
persistence decision (ENG-0002) is exactly the soil: the registry and inventory **snapshots** become
durable (yielding real historical diff, not just the session diff in `Store.cs`), while secrets stay
out of plaintext via `ISecretStore`.

- **Datastore:** SQLite by default (single file in a mounted volume), Postgres as an opt-in for multi-instance. EF Core. **Single-operator — no `tenant_id`** (ENG-0006).
- **Persisted:** provider/host registry, inventory snapshots (→ real historical diff, not just the session diff in [Store.cs](../../Store.cs)), **operations** jobs + steps + logs (migrate/deploy/backup/restore), the PKI allow/deny list (ENG-0005), audit trail. Secrets are stored **only via `ISecretStore`** — the registry references them by `secret_ref`, never embeds values.
- **Secret handling (ENG-0002):** an **`ISecretStore` provider abstraction** (`get`/`set`/`rotate`/`delete`, audit-on-access). v1 impl = **app-native envelope encryption** — values encrypted with **AES-256-GCM** in SQLite under a per-DB **DEK**, the DEK wrapped by a **KEK injected at runtime** (Docker/Podman secret, systemd `LoadCredential`, or env var; operator-passphrase is an opt-in mode). Vault/OpenBao + Azure Key Vault are optional providers behind the same interface, later. The Phase-1 zero-on-dispose discipline in [Models.cs:121](../../VMentory.Core/Models.cs#L121) carries forward for in-memory secret material.

> **Built (slice 3, commit `a423a62`).** The host **registry** and inventory **snapshots** now exist
> in [`VMentory.Core/Persistence`](../../VMentory.Core/Persistence) (EF Core + SQLite, `Initial`
> migration): `HostRegistrationEntity` (`id, platform, address, use_global_creds, added_at` — **no
> creds, no runtime/scan state**) and `InventorySnapshotEntity` (`id, host_id` FK cascade, `taken_at`,
> `payload_json` = the scanned `Host` inventory serialized, creds excluded via `[JsonIgnore]`), behind
> `IInventoryStore`/`EfInventoryStore`. The in-memory `Store` stays the working set: seeded from the
> registry at startup and **written through** on host add/remove and snapshot-on-successful-scan. **The
> diff is now snapshot-fed** — its "previous" is the latest two persisted snapshots, ordered by the
> autoincrement `Id` (SQLite can't `ORDER BY` a `DateTimeOffset`), replacing the in-memory pre-scan
> copy. Persistence is **always-on in real mode**, off under `--mock` (which registers no `DbContext`
> and touches no disk); DB path = `VMENTORY_DB` env var (mounted volume) else
> `%LocalAppData%\VMentory\vmentory.db`. `/api/quit` is now **graceful shutdown only** — it still zeroes
> in-memory secrets but **does not purge** the persisted registry/snapshots.
> **Landed in slice (2):** `audit_event` and `app_user` tables (`AddAuth` migration).
> **Landed in slice (3):** `SecretEntity`/`DekEntity` (`AddSecrets` migration); credentials now
> persist via `ISecretStore` (ENG-0002) and are restored at startup when `VMENTORY_KEK` is set.
> **Still deferred to their owning slices:** `operations`/`jobs`, `agent_identity` (PKI).

### 4. Operations engine (generalized from the migration engine · ENG-0006)
The original "migration job engine" generalizes into a **general operations engine**:
**migrate, deploy, backup, and restore are all persisted, resumable, dry-runnable jobs** on the
same machinery — a **persisted, resumable DAG of steps** with live progress over SSE (reuse
[EventHub.cs](../../EventHub.cs)). The migration spec
([specs/migration-job-model.md](specs/migration-job-model.md)) defines the DAG mechanics in depth;
Deploy/Backup add their own step graphs against the same engine.

Reference flow — **Hyper-V → Proxmox migration, single VM** (the proven `migrate-vm` path):
1. **Precheck** — capability match, free space on target storage, guest OS, BIOS/UEFI (Gen2 → OVMF), power state, **checkpoint state** (merge `.avhdx` → flat `.vhdx`).
2. **Quiesce** — **operator choice** per job (ENG-0006): graceful shutdown vs checkpoint/live, with explicit caveats (static frontend → favor uptime; DB server → favor consistency). Not hardcoded.
3. **Export / transfer** — agent locates and exposes the flat VHDX(s); disk transfer is a **Proxmox-node concern** (CIFS mount, out-of-band — not over the control channel), per ENG-0001.
4. **Provision** — create VM shell via PVE API (`POST /nodes/{node}/qemu`), matching vCPU/RAM/firmware (`bios=ovmf`+`efidisk0` for Gen2).
5. **Import disk** — **`qm importdisk` on the PVE node** (VHDX→raw); attach, set boot order, NIC model (virtio), preserve MAC. **This is the proven baseline (ENG-0001), not virt-v2v.**
6. **Guest fix** — targeted guest customization (Linux netplan match-by-MAC; Windows virtio/SATA driver handling). Shared with the Deploy guest-customization layer (ENG-0006). **virt-v2v is an optional enhancement** to automate Windows virtio injection — not required.
7. **First boot + validate** — boot **isolated** (link down), confirm via console; prove the source is off before bringing the copy onto the network (the skill's verification rule).
8. **Cutover** — flip DNS/notes, mark source decommissioned (don't auto-delete source — leave rollback intact).

Engine requirements: each step **idempotent** and **resumable** (survives a Core *or* agent restart mid-job), structured per-step logs persisted, hard stop + rollback hooks, dry-run mode. Bidirectional and same-platform migration are subgraphs of the same engine; **PVE→HV is a distinct later effort** (reverse driver handling, qcow2→VHDX, Gen2/UEFI — ENG-0006).

### 5. Security (this is now a hosted service, not loopback)
The Phase-1 model — random port, session token, 127.0.0.1 only ([Program.cs:82](../../Program.cs#L82)) — does **not** survive becoming a shared hosted service. The container runtime contract is in [Deployment model](#deployment-model-eng-0010--containerized-web-first-install-nothing); the auth/transport posture is:
- **Console authn/authz (ENG-0008, Decided 2026-06-16 — BUILT re-baselined slice (2), 2026-06-18):** the single session token is **retired**; cookie-based login (`vmentory_session`, HttpOnly Secure SameSite=Strict, 12h sliding) is live. **Fixed built-in roles** (Admin / VmOperator / BackupOperator / Viewer) over an **internal pillar×verb permission catalog** that **reuses `ProviderCapability`** plus a **small console-permission set** (manage-credentials, manage-enrollment, view-audit, manage-users); role→permission mapping is **data (`RbacCatalog.cs`), not hardcoded `if`s**, so a custom-roles UI is a non-breaking later addition. **Identity = local accounts** (PBKDF2-SHA256, work-factor in hash, KEK-wrapped per ENG-0002), **OIDC seam later** behind the same chokepoint. Enforcement is a **single audited authorization chokepoint** in `VMentory.Web`/API **and** the operations engine (org-wide scope in the first cut; per-host scoping is a later catalog extension). This console RBAC is **distinct from** the agent's constrained-verb authz (ENG-0004): roles gate *which operator* may invoke *which pillar/verb*; the agent independently constrains *what verbs exist at all*. Browser↔Core is **Core-terminated HTTPS** (ENG-0010).
- **Core → Proxmox (ENG-0009 — the primary path, no agent):** outbound only. **Scoped, privilege-separated PVE API token** (`Authorization: PVEAPIToken=…`, ACL-restricted — not a root ticket) for orchestration/lifecycle/provisioning/stats/native migration; a **constrained, forced-command, dedicated-account SSH key** for `qm importdisk` / `qemu-img` / optional `virt-v2v` / on-node guest-root edits. Both secrets live in `ISecretStore` and are revocable in the PVE UI. Nodes never connect inbound.
- **Core ↔ Agent (ENG-0004/0005) — Hyper-V migration source only:** **gRPC over HTTP/2 + mTLS**, mutual. Agent identity is an enrolled client cert; the agent pins Core's root. Certs are **short-lived + auto-renewed** over the mTLS channel; revocation = **stop-renew + a registry allow/deny list** (no CRL/OCSP). This trust domain and its private CA are **separate from the dashboard TLS** (ENG-0010) and are **demoted off the 2.0 critical path** to the migration slice (ENG-0009).
- **PKI ownership (ENG-0005):** a **private CA inside Core** (root + intermediate) signs agent CSRs at enrollment; an external-CA seam (AD CS) is deferred. The CA private key lives in `ISecretStore` (the natural case for the opt-in operator-passphrase KEK mode).
- **Enrollment (ENG-0003/0005):** manual install + a **single-use, short-lived enrollment token** → CSR (key never leaves the host) → Core-intermediate-signed client cert + the root to pin. VMentory never receives an admin credential.
- **Agent is a constrained verb executor (ENG-0004):** a fixed, versioned verb set — never arbitrary PowerShell/RCE. Signed binaries; every executed verb is audit-logged back to Core's history store.
- **Provider secrets (ENG-0002):** from `ISecretStore`, scoped — **PVE API tokens** (privilege-separated, not root ticket), **dedicated SSH keys** (ideally forced-command), per the tokens-over-passwords principle.
- **Audit log** for every write/management/migration/deploy/backup action.

---

## What carries over vs. what gets rebuilt

| Phase 1 asset | Phase 2 fate |
|---|---|
| `Models.cs` domain types | **Generalize** into `VMentory.Core` domain |
| `Scanner.cs` / `Reachability.cs` PowerShell logic | **Reimplement natively in .NET** inside `VMentory.Agent` (runs locally on host, ENG-0001/0004) — no Python, no winrun.py in the product |
| `Store.cs` in-memory state | **Kept as the in-memory working set, now seeded from + written through to the persistence layer** (slice 3) — not replaced; the diff logic is retained but fed from persisted snapshots |
| `EventHub.cs` SSE | **Keep** — reuse for job/scan progress |
| `Exporter.cs` | **Keep/extend** |
| `wwwroot/index.html` SPA | **Evolve** — add provider switching, management verbs, the migration/deploy/backup wizards (design agent owns this) |
| Session-token + loopback security | **Replaced** — loopback/random-port (re-baselined slice (1): `0.0.0.0` bind + Core-terminated HTTPS); session token **retired** in re-baselined slice (2) by cookie-based login + RBAC (ENG-0008). |
| `Updater.cs` GitHub auto-update | **Re-scope (pending)** — still GitHub-exe auto-update after slice (1); to become container image tags for Core; the **agent self-updates over its own mTLS channel with watchdog rollback** (ENG-0004), reusing this apply-on-launch pattern |
| `migrate-vm/` skill + winrun.py | **Behavioral reference spec** for the agent's migration verbs (ENG-0001) — its step graph, safety rules, and the `centralized-access.md` 401 gotcha list inform the agent design; the WinRM/Python code itself does **not** ship |

---

## Open questions

**Resolved (do not re-open):**
- ~~Agent transport: gRPC vs HTTP+JSON?~~ → **gRPC over HTTP/2 + mTLS** (ENG-0004).
- ~~Multi-tenant or single-operator?~~ → **Single-operator**, no `tenant_id` (ENG-0006).
- ~~mTLS PKI ownership?~~ → **Private CA inside Core**, root+intermediate, short-lived certs (ENG-0005).
- ~~Secret-store v1 target?~~ → **`ISecretStore` + app-native envelope encryption** (ENG-0002).
- ~~Agent install model / self-update?~~ → **Manual install + enrollment token** (ENG-0003); **mTLS self-update + watchdog** (ENG-0004).
- ~~How does Core reach Proxmox — agent vs API/SSH?~~ → **Native PVE REST API primary + constrained SSH key; no node agent** (ENG-0009). This also **demotes/scopes the agent foundation to Hyper-V**.
- ~~Console RBAC / scoped roles model?~~ → **Fixed roles over a pillar×verb catalog (reusing `ProviderCapability`); local accounts now, OIDC seam later; single audited chokepoint before any write verb; login early in the deployment slice** (ENG-0008).
- ~~Core deployment & runtime (bind/TLS/KEK/volumes/auth swap)?~~ → **Containerized, `0.0.0.0` bind, Core-terminated HTTPS, runtime KEK/TLS injection, SQLite on a mounted volume, session-token → login+RBAC** (ENG-0010).

**Still open (need a human decision; downstream specs proceed against the current recommendation):**
- **Conversion-host placement** — run optional `virt-v2v`/`qemu-img` on the PVE node over SSH, or a dedicated conversion container? Shapes staging topology. (The *baseline* `qm importdisk` runs on the PVE node — this only concerns the optional conversion enhancement.)
- **Storage / repository layer placement** (ENG-0006) — ISO/golden-image/backup repos + dedup on a Core volume, a dedicated storage host, or native platform storage? The biggest new gap; a future ENG topic.
- **Guest-customization engine** (ENG-0006) — cloud-init / sysprep / unattend vs platform-native, shared by Deploy and Migrate. A future ENG topic.
- **Backup buy-vs-build** (ENG-0006) — orchestrate Proxmox Backup Server / Veeam vs build a native dedup/replication engine. **Explicitly deferred and out of release 1** (ENG-0007) — decided when pillars 1–3 are further along. A future ENG topic.
- **PVE→HV reverse migration** (ENG-0006) — the distinct later effort (reverse drivers, qcow2→VHDX, Gen2/UEFI). **Out of release 1** (ENG-0007). A future ENG topic.
- **PVE node TLS / SSH trust model** ([proxmox-integration.md](specs/proxmox-integration.md#5-open-questions-need-a-human-decision)) — the container's SSH-client known-hosts/pinning strategy + SSH hardening shape (dedicated account + `sudo` vs root forced-command) are **spec details** under ENG-0009, not reopened decisions. Plus **DB at-rest encryption / snapshot retention** ([persistence-and-security.md](specs/persistence-and-security.md#7-open-questions-need-a-human-decision)).
- **In-guest customization** (ENG-0009 sub-question) — whether any sysprep/cloud-init/post-deploy case must run *inside* a guest rather than via node-side mount-and-edit; if so it is a *guest* agent, a separate future ENG topic — it does **not** revive a PVE *node* agent.

See `docs/phase2/specs/` for the expanded component specs and
[`docs/engineering/REGISTER.md`](../engineering/REGISTER.md) for the decision register.
