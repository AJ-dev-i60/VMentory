# VMentory Phase 2 — Target Architecture

> Status: **draft** · Owner: codebase side · Last decisions locked: 2026-06-16 (ENG-0001..0007)
>
> Phase 1 = single-exe, Windows-only, read-only, ephemeral Hyper-V inventory.
> Phase 2 = a **container-based, single-operator, multi-platform (Hyper-V + Proxmox) platform**
> with Windows + Linux on-device agents, delivering **four product pillars on one shared
> foundation** (ENG-0006), **weighted and sequenced toward a Proxmox-first North Star** (ENG-0007).

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

## Locked decisions

Each cites the engineering decision (`ENG-NNNN`) that produced it; see
[`docs/engineering/REGISTER.md`](../engineering/REGISTER.md). Don't silently contradict a **Decided**
item — supersede it through the engineering protocol first.

| # | Decision | Choice | Source |
|---|---|---|---|
| 1 | How the hosted tool reaches Hyper-V | **Agent installed on the Windows host**, reimplemented **natively in .NET** (no winrun.py / Python in the product). Transport = **gRPC over HTTP/2 + mTLS**. The agent drives migration from day one — there is **no winrun.py fallback**. | ENG-0001, ENG-0004 |
| 2 | Secret model | **`ISecretStore` provider abstraction** + v1 **app-native envelope encryption** (AES-256-GCM in SQLite, DEK wrapped by a **runtime-injected KEK**); secrets **never plaintext at rest**. Binding principle: **prefer scoped keys/tokens over passwords**. Vault/OpenBao + Azure KV are optional providers later. | ENG-0002 |
| 3 | Migration tool orchestration | **Orchestrate proven tools.** The validated path is **`qm importdisk` on the PVE node** (reads VHDX off a CIFS mount, converts VHDX→raw) + targeted guest fixes. **virt-v2v is OPTIONAL** (Windows virtio injection), **not the baseline**. | ENG-0001, ENG-0006 |
| 4 | State model | **Hybrid** — registry/jobs/history persisted (SQLite/EF Core); secrets via `ISecretStore` (decision #2). | ENG-0002 |
| 5 | Agent install / onboarding | **Manual / org-managed only** (MSI / GPO / SCCM / Intune) + a short-lived **enrollment token**. **No remote push-install**; VMentory never receives an admin credential. | ENG-0003 |
| 6 | Agent runtime | **NativeAOT single binary**, Windows Service / systemd unit; **constrained verb executor** (fixed versioned verb set, never arbitrary PowerShell/RCE); **gMSA-preferred** identity (local-account fallback); **self-update over its own mTLS channel** with watchdog rollback. | ENG-0004 |
| 7 | mTLS PKI | **Private CA inside Core** (external-CA seam later); **root + intermediate**; **short-lived agent certs + auto-renew** over the mTLS channel; revocation = stop-renew + a registry allow/deny list (**no CRL/OCSP**). CA key in `ISecretStore`. | ENG-0005 |
| 8 | Product scope | **Four pillars on one shared foundation**; conceptually **Observe → Migrate → Deploy → Backup**; **bidirectional migration end-state, HV→PVE first**; **backup buy-vs-build deferred**. | ENG-0006 |
| 9 | Product strategy & release scope | **Proxmox-first North Star** (retire Hyper-V → Proxmox). **Incremental release cadence** — each milestone (2.0→2.4) is its own usable release; **v2.0 = planted Observe**. Pillars **weighted/sequenced by effort**: **Observe (plant) → Proxmox management/Deploy → HV→PVE migration**. **Hyper-V = light management** (start/stop/reconfigure), not source-only, not full parity → **the `IVirtualizationProvider` capability model must allow management verbs on the HV provider**. **PVE→HV reverse migration and Backup/Restore are out of release 1.** Refines/sequences ENG-0006, does not supersede it. | ENG-0007 |

These pin the rest of the design. If one changes, supersede it in `docs/engineering/` and revisit
this file first.

---

## Topology

```
                         ┌─────────────────────────────────────────────────┐
                         │  VMentory Core  (Linux container)                │
                         │  ASP.NET Core 8 · provider model · ops engine    │
   browser ──https──▶    │  ┌─────────┐  ┌──────────┐  ┌──────────────────┐ │
   (authenticated UI)    │  │ Web/API │  │ Provider │  │ Operations engine│ │
                         │  │  + SSE  │  │ registry │  │ (migrate/deploy/ │ │
                         │  └─────────┘  └────┬─────┘  │  backup/restore) │ │
                         │  ┌───────────────┐ │        └────────┬─────────┘ │
                         │  │ ISecretStore  │ │ ┌──────────────┐│           │
                         │  │ + internal CA │ │ │ storage/repo ││ scheduler │
                         │  └───────────────┘ │ │ (ISO/image/  ││           │
                         │   persistence       │ │  backup) ⌁   ││           │
                         │   (SQLite/Postgres) │ └──────────────┘│           │
                         └───────────┬─────────┴────────┬────────┼──────────┘
                                     │                  │        │
                  gRPC/HTTP2 + mTLS  │                  │ REST(8006)+SSH
                                     ▼                  ▼        ▼
                      ┌──────────────────────────┐   ┌────────────────────────┐
                      │ VMentory Agent           │   │ Proxmox VE node(s)     │
                      │ (NativeAOT service on the │   │ API token (scoped)     │
                      │  Hyper-V host; gMSA/local)│   │ SSH for qm importdisk  │
                      │ constrained verb executor │   │ / qemu-img / (virt-v2v │
                      │ inventory→lifecycle→      │   │  optional)             │
                      │ migrate→deploy→backup     │   │                        │
                      └──────────────────────────┘   └────────────────────────┘
                       (⌁ storage/repo placement undecided — see Open questions)
```

- **Core** is the only thing the user talks to. It holds no platform-specific transport logic itself — it talks to *providers*.
- **Hyper-V** is reached through a **NativeAOT agent** that runs *on* the host (ENG-0001/0003/0004). This relocates today's `Scanner.cs` / `Reachability.cs` logic — reimplemented **natively in .NET** — to where the Hyper-V module already works, eliminating Linux→WinRM auth pain, and replacing a stored Windows domain password with a scoped, revocable **mTLS client cert** (ENG-0001). The agent is a **constrained verb executor**, never a remote shell (ENG-0004). Its verb catalog spans **inventory, lifecycle (light management — start/stop/reconfigure), and migration-source** verbs (ENG-0007); it deliberately does **not** gain Deploy or Backup/Restore verbs — those are Proxmox-side, per the "light, not parity" Hyper-V scope.
- **Proxmox** needs no agent: REST API (scoped token auth) for orchestration + stats, SSH for `qm importdisk` / `qemu-img` / optional `virt-v2v`. (An optional conversion-host can come later if we want conversion off the PVE node — still open.)
- **The agent is the gate for migration (ENG-0001).** Because there is no winrun.py fallback, "the agent can drive guest control + the migration step graph" must land in 2.0/2.2 before 2.3 migration starts.

---

## Component breakdown

### 1. VMentory Core
ASP.NET Core 8 (keep the stack — it's already cross-platform and containerizes cleanly). Split the current single project into:

| Project | Responsibility |
|---|---|
| `VMentory.Core` | domain model, provider abstraction, operations engine, `ISecretStore`, internal CA, persistence |
| `VMentory.Providers.HyperV` | talks to the Windows agent (gRPC/mTLS) |
| `VMentory.Providers.Proxmox` | PVE REST client + SSH executor |
| `VMentory.Web` | minimal API, SSE, auth, serves the SPA |
| `VMentory.Agent` | the NativeAOT on-device service — Windows (Hyper-V) and Linux host roles (separate deployable, ENG-0004) |

> **Rename `HyperInventory` → `VMentory.*`.** The namespace is a Phase-1 fossil and will read wrong the moment Proxmox lands. Do this in the 2.0 foundation work, before new code piles on it.

### 2. Provider abstraction
The pivot from "Hyper-V tool" to "platform" lives here.

```csharp
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

- Today's `Host`/`Vm`/`Vhd`/`Volume` ([Models.cs](../../Models.cs)) are already close to platform-neutral — generalize them into the `VMentory.Core` domain, add a `Platform` discriminator and capability flags, drop Hyper-V-only assumptions (e.g. `Generation`, `IntegrationServices` become provider-specific extensions).

#### Capability model — management verbs are capability-gated **per provider** (ENG-0007)

This is the part the 2.0 foundation depends on, so it is specified concretely here. **Proxmox is the
primary, full management target; Hyper-V advertises only light management** (start / stop /
reconfigure) per the ENG-0007 "light, not parity" rule. The abstraction must therefore let **each
provider declare which management verbs it supports**, and **every caller — the operations engine, the
API, and the UI — must honor those flags**. A capability model designed as "Hyper-V = read + export
only, Proxmox = read + write" would have to be reworked later; do **not** build it that way.

Shape: a `[Flags]` enum surfaced per provider via `ProviderCapabilities`, checked before any verb is
offered or dispatched.

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

Indicative per-provider advertisement (exact sets pinned as each milestone lands):

| Capability group | `HyperVProvider` | `ProxmoxProvider` |
|---|---|---|
| Observe (Inventory / LiveStats / HistoricalStats) | ✅ (planted foundation) | ✅ |
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
- **Secret handling (ENG-0002):** an **`ISecretStore` provider abstraction** (`get`/`set`/`rotate`/`delete`, audit-on-access). v1 impl = **app-native envelope encryption** — values encrypted with **AES-256-GCM** in SQLite under a per-DB **DEK**, the DEK wrapped by a **KEK injected at runtime** (Docker/Podman secret, systemd `LoadCredential`, or env var; operator-passphrase is an opt-in mode). Vault/OpenBao + Azure Key Vault are optional providers behind the same interface, later. The Phase-1 zero-on-dispose discipline in [Models.cs:121](../../Models.cs#L121) carries forward for in-memory secret material.

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
The Phase-1 model — random port, session token, 127.0.0.1 only ([Program.cs:82](../../Program.cs#L82)) — does **not** survive becoming a shared hosted service. Phase 2:
- **User auth** for the UI: at minimum a configured admin credential (2.0); roles later; OIDC/SSO optional. Replace the single session token; browser↔Core is HTTPS.
- **Core ↔ Agent (ENG-0004/0005):** **gRPC over HTTP/2 + mTLS**, mutual. Agent identity is an enrolled client cert; the agent pins Core's root. Certs are **short-lived + auto-renewed** over the mTLS channel; revocation = **stop-renew + a registry allow/deny list** (no CRL/OCSP).
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
| `Store.cs` in-memory state | **Replaced** by persistence layer; keep the diff logic, back it with snapshots |
| `EventHub.cs` SSE | **Keep** — reuse for job/scan progress |
| `Exporter.cs` | **Keep/extend** |
| `wwwroot/index.html` SPA | **Evolve** — add provider switching, management verbs, the migration/deploy/backup wizards (design agent owns this) |
| Session-token + loopback security | **Replaced** by real auth |
| `Updater.cs` GitHub auto-update | **Re-scope** — container image tags for Core; the **agent self-updates over its own mTLS channel with watchdog rollback** (ENG-0004), reusing this apply-on-launch pattern |
| `migrate-vm/` skill + winrun.py | **Behavioral reference spec** for the agent's migration verbs (ENG-0001) — its step graph, safety rules, and the `centralized-access.md` 401 gotcha list inform the agent design; the WinRM/Python code itself does **not** ship |

---

## Open questions

**Resolved (do not re-open):**
- ~~Agent transport: gRPC vs HTTP+JSON?~~ → **gRPC over HTTP/2 + mTLS** (ENG-0004).
- ~~Multi-tenant or single-operator?~~ → **Single-operator**, no `tenant_id` (ENG-0006).
- ~~mTLS PKI ownership?~~ → **Private CA inside Core**, root+intermediate, short-lived certs (ENG-0005).
- ~~Secret-store v1 target?~~ → **`ISecretStore` + app-native envelope encryption** (ENG-0002).
- ~~Agent install model / self-update?~~ → **Manual install + enrollment token** (ENG-0003); **mTLS self-update + watchdog** (ENG-0004).

**Still open (need a human decision; downstream specs proceed against the current recommendation):**
- **Conversion-host placement** — run optional `virt-v2v`/`qemu-img` on the PVE node over SSH, or a dedicated conversion container? Shapes staging topology. (The *baseline* `qm importdisk` runs on the PVE node — this only concerns the optional conversion enhancement.)
- **Storage / repository layer placement** (ENG-0006) — ISO/golden-image/backup repos + dedup on a Core volume, a dedicated storage host, or native platform storage? The biggest new gap; a future ENG topic.
- **Guest-customization engine** (ENG-0006) — cloud-init / sysprep / unattend vs platform-native, shared by Deploy and Migrate. A future ENG topic.
- **Backup buy-vs-build** (ENG-0006) — orchestrate Proxmox Backup Server / Veeam vs build a native dedup/replication engine. **Explicitly deferred and out of release 1** (ENG-0007) — decided when pillars 1–3 are further along. A future ENG topic.
- **PVE→HV reverse migration** (ENG-0006) — the distinct later effort (reverse drivers, qcow2→VHDX, Gen2/UEFI). **Out of release 1** (ENG-0007). A future ENG topic.
- **PVE node TLS trust model** ([proxmox-integration.md](specs/proxmox-integration.md#5-open-questions-need-a-human-decision)) and **DB at-rest encryption / snapshot retention** ([persistence-and-security.md](specs/persistence-and-security.md#7-open-questions-need-a-human-decision)).

See `docs/phase2/specs/` for the expanded component specs and
[`docs/engineering/REGISTER.md`](../engineering/REGISTER.md) for the decision register.
