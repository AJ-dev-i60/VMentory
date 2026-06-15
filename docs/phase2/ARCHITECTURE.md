# VMentory Phase 2 — Target Architecture

> Status: **draft** · Owner: codebase side · Last decisions locked: 2026-06-15
>
> Phase 1 = single-exe, Windows-only, read-only, ephemeral Hyper-V inventory.
> Phase 2 = **hosted, multi-platform, read/write management + migration** tool.

This document is the spine. The documentation agent expands the per-component specs
under `docs/phase2/specs/`; the UI design agent works the visual side under `design/`.

---

## Locked decisions (2026-06-15)

| # | Decision | Choice |
|---|---|---|
| 1 | How the hosted tool reaches Hyper-V | **Agent installed on the Windows host** (HTTP/gRPC), not remote WinRM from Linux |
| 2 | State model | **Hybrid** — persist registry/jobs/history; secrets live in a vault/secret store, never plaintext at rest |
| 3 | Migration approach | **Orchestrate proven tools** (virt-v2v, qemu-img, `qm importdisk`) — don't build conversion from scratch |

These three pin the rest of the design. If one changes, revisit this file first.

---

## Topology

```
                         ┌─────────────────────────────────────────────┐
                         │  VMentory Core  (Linux container)            │
                         │  ASP.NET Core 8 · provider model · job engine│
   browser ──https──▶    │  ┌─────────┐  ┌──────────┐  ┌──────────────┐ │
   (authenticated UI)    │  │ Web/API │  │ Provider │  │ Migration    │ │
                         │  │  + SSE  │  │ registry │  │ job engine   │ │
                         │  └─────────┘  └────┬─────┘  └──────┬───────┘ │
                         │       persistence (SQLite/Postgres)│         │
                         └───────────┬──────────────┬─────────┼─────────┘
                                     │              │         │
                    mTLS / token     │              │ REST(8006)+SSH
                                     ▼              ▼         ▼
                      ┌──────────────────────┐   ┌────────────────────────┐
                      │ VMentory Agent        │   │ Proxmox VE node(s)     │
                      │ (Windows service on   │   │ API token + root@pam   │
                      │  the Hyper-V host)    │   │ SSH for disk/virt-v2v  │
                      │ runs Hyper-V cmdlets  │   │                        │
                      └──────────────────────┘   └────────────────────────┘
```

- **Core** is the only thing the user talks to. It holds no platform-specific transport logic itself — it talks to *providers*.
- **Hyper-V** is reached through an **agent** that runs *on* the host. This relocates today's `Scanner.cs` / `Reachability.cs` PowerShell logic to where PowerShell + WinRM already work natively — no Linux→WinRM auth pain, no TrustedHosts gymnastics.
- **Proxmox** needs no agent: REST API (token auth) for orchestration + stats, SSH for disk import / `virt-v2v` / `qemu-img convert`. (An optional conversion-host agent can come later if we want conversion off the PVE node.)

---

## Component breakdown

### 1. VMentory Core
ASP.NET Core 8 (keep the stack — it's already cross-platform and containerizes cleanly). Split the current single project into:

| Project | Responsibility |
|---|---|
| `VMentory.Core` | domain model, provider abstraction, job engine, persistence |
| `VMentory.Providers.HyperV` | talks to the Windows agent |
| `VMentory.Providers.Proxmox` | PVE REST client + SSH executor |
| `VMentory.Web` | minimal API, SSE, auth, serves the SPA |
| `VMentory.Agent` | the Windows-host service (separate deployable) |

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
- **Capabilities gate the UI.** A provider advertises what it supports; the UI only shows verbs the target can do. Avoids "Migrate" buttons that 500.

### 3. Persistence (hybrid)
- **Datastore:** SQLite by default (single file in a mounted volume), Postgres as an opt-in for multi-instance. EF Core.
- **Persisted:** provider/host registry, inventory snapshots (→ real historical diff, not just the session diff in [Store.cs](../../Store.cs)), migration jobs + steps + logs, audit trail.
- **NOT persisted in the DB:** credentials, API tokens, SSH keys. These come from Docker secrets / env / an external vault and are decrypted into memory only. The Phase-1 discipline in [Models.cs:121](../../Models.cs#L121) (`Credentials` zeroes its bytes) carries forward.

### 4. Migration job engine
A migration is a **persisted, resumable DAG of steps** with live progress over SSE (reuse [EventHub.cs](../../EventHub.cs)).

Reference flow — **Hyper-V → Proxmox, single VM:**
1. **Precheck** — capability match, free space on target storage, guest OS supported, BIOS/UEFI (Gen2 → OVMF), power state.
2. **Quiesce** — graceful shutdown or checkpoint (configurable; warn on live-data risk).
3. **Export** — agent locates VHDX(s); copy/stream to staging.
4. **Convert** — `virt-v2v` (preferred — does virtio driver injection for Windows guests) or `qemu-img convert` for the raw disk path.
5. **Provision** — create VM shell via PVE API (`POST /nodes/{node}/qemu`), matching vCPU/RAM/firmware.
6. **Attach** — `qm importdisk` / attach converted disk; set boot order, NIC model (virtio), EFI disk if UEFI.
7. **First boot + validate** — power on, confirm guest agent / network.
8. **Cutover** — flip DNS/notes, mark source decommissioned (don't auto-delete source — leave rollback intact).

Engine requirements: each step **idempotent** and **resumable**, structured per-step logs persisted, hard stop + rollback hooks, dry-run mode. Same-platform migration (HV↔HV, PVE↔PVE via native APIs) is a *simpler* subgraph of the same engine.

### 5. Security (this is now a hosted service, not loopback)
The Phase-1 model — random port, session token, 127.0.0.1 only ([Program.cs:82](../../Program.cs#L82)) — does **not** survive becoming a shared hosted service. Phase 2 needs:
- **User auth** for the UI: at minimum a configured admin credential; ideally OIDC/SSO. Replace the single session token.
- **Core ↔ Agent:** mTLS or short-lived signed tokens; the agent only accepts the Core's identity.
- **Provider secrets** from a secret store, scoped (PVE API tokens privilege-separated, not root ticket where avoidable).
- **Audit log** for every write/management/migration action.

---

## What carries over vs. what gets rebuilt

| Phase 1 asset | Phase 2 fate |
|---|---|
| `Models.cs` domain types | **Generalize** into `VMentory.Core` domain |
| `Scanner.cs` / `Reachability.cs` PowerShell logic | **Relocate** into `VMentory.Agent` (runs locally on host) |
| `Store.cs` in-memory state | **Replaced** by persistence layer; keep the diff logic, back it with snapshots |
| `EventHub.cs` SSE | **Keep** — reuse for job/scan progress |
| `Exporter.cs` | **Keep/extend** |
| `wwwroot/index.html` SPA | **Evolve** — add provider switching, management verbs, migration wizard (design agent owns this) |
| Session-token + loopback security | **Replaced** by real auth |
| `Updater.cs` GitHub auto-update | **Re-scope** — container image tags instead of exe-replace; agent gets its own update path |

---

## Open questions for the spec agents
- Agent transport: gRPC vs plain HTTP+JSON? (Lean gRPC for streaming logs, but HTTP is simpler to debug.)
- Conversion host: run `virt-v2v` on the PVE node over SSH, or a dedicated conversion container?
- Multi-tenant or single-operator? (Affects auth depth.)

See `docs/phase2/specs/` for the expanded component specs.
