# Migration Job Model

> Component spec · expands [ARCHITECTURE.md §4 Migration job engine](../ARCHITECTURE.md#4-migration-job-engine).
> Anchored to **Decision 3**: orchestrate proven tools (`virt-v2v`, `qemu-img`, `qm importdisk`),
> don't build conversion from scratch. Delivered in [ROADMAP.md §2.3](../ROADMAP.md).

A migration is a **persisted, resumable DAG of steps**. The engine's job is orchestration,
sequencing, idempotency, logging, and rollback — **not** disk conversion. Conversion is delegated
to battle-tested tools invoked through the providers
([provider-abstraction.md §2](provider-abstraction.md#2-the-interface)) over their respective
channels ([agent-protocol.md](agent-protocol.md), [proxmox-integration.md §3](proxmox-integration.md#3-rest-vs-ssh--the-boundary)).

---

## 1. Why a persisted DAG

A migration runs for minutes to hours, touches two platforms, and must survive a Core restart
mid-flight without orphaning a half-created VM or a half-copied 200 GB disk. Phase-1 state is
in-memory and ephemeral ([Store.cs:5](../../../Store.cs#L5) "no disk writes, no persistence") —
that model cannot back a long migration. So:

- **Job + Step + Log rows are persisted** ([persistence-and-security.md §1](persistence-and-security.md#1-schema-sketch)).
- Each **step is a node** with declared **dependencies** (the DAG), a status
  (`Pending`/`Running`/`Succeeded`/`Failed`/`Skipped`/`RolledBack`), inputs, and outputs.
- On restart, the engine reloads the job and resumes from the first non-`Succeeded` step whose
  dependencies are satisfied — hence every step must be **idempotent** and **resumable** (§7).

DAG, not a flat list, because same-platform migration is "a *simpler* subgraph of the same engine"
([ARCHITECTURE.md §4](../ARCHITECTURE.md#4-migration-job-engine)) — HV↔HV / PVE↔PVE
([ROADMAP.md §2.4](../ROADMAP.md)) reuse the convert/transfer nodes by pruning them, and batch
migrations ([ROADMAP.md §2.4](../ROADMAP.md)) compose multiple per-VM subgraphs.

---

## 2. Job lifecycle & modes

- **Dry-run** ([ARCHITECTURE.md §4](../ARCHITECTURE.md#4-migration-job-engine), ROADMAP 2.3 exit
  bar): execute Precheck + plan every step, mutate nothing on either platform. Required before any
  real run in the wizard.
- **Run**: execute steps in dependency order, streaming progress.
- **Stop**: hard stop + rollback hooks (§7). Every step takes a `CancellationToken` — the engine
  threads CTs end-to-end exactly as Phase 1 does
  ([Reachability.cs:155](../../../Reachability.cs#L155), [Scanner.cs:56](../../../Scanner.cs#L56)).
- **Resume**: after crash/restart, reload and continue.

---

## 3. Step flow: Hyper-V → Proxmox

The reference flow, straight from [ARCHITECTURE.md §4](../ARCHITECTURE.md#4-migration-job-engine),
expanded with the owning channel and idempotency note for each step:

| # | Step | Channel / tool | Idempotency key | Rollback |
|---|---|---|---|---|
| 1 | **Precheck** | both providers' `Capabilities` + REST/agent reads | read-only | none needed |
| 2 | **Quiesce** | agent `Lifecycle` (shutdown or checkpoint) | record prior power state | restart source to prior state |
| 3 | **Export** | agent `ExportDisk` → staging (out-of-band, [agent-protocol.md §6](agent-protocol.md#6-disk-transfer-is-out-of-band)) | staged file + checksum present → skip | delete staged artifacts |
| 4 | **Convert** | `virt-v2v` (preferred) / `qemu-img convert` over SSH ([proxmox-integration.md §3](proxmox-integration.md#3-rest-vs-ssh--the-boundary)) | converted image + checksum present → skip | delete converted image |
| 5 | **Provision** | PVE REST `POST /nodes/{n}/qemu` ([proxmox-integration.md §2](proxmox-integration.md#2-rest-surface-used-by-milestone)) | vmid exists with our marker → reuse | destroy created shell |
| 6 | **Attach** | `qm importdisk` (SSH) + `PUT /config` boot/NIC/EFI (REST) | disk already attached to vmid → skip | detach + delete imported volume |
| 7 | **First boot + validate** | PVE REST `status/start` + guest-agent/network check | boot already validated flag | stop the VM |
| 8 | **Cutover** | flip DNS/notes, mark source decommissioned — **never auto-delete source** | cutover-done flag | revert notes/DNS; source is still intact |

**Precheck (step 1) detail** — the gate that prevents doomed runs:
capability match (source `DiskExport`, target `DiskImport`+`CreateVmShell`,
[provider-abstraction.md §3](provider-abstraction.md#3-capability-model)); target storage free
space (`/nodes/{n}/storage`, [proxmox-integration.md §2](proxmox-integration.md#2-rest-surface-used-by-milestone));
guest OS supported by `virt-v2v`; **firmware mapping** Gen2→OVMF
([Models.cs:39](../../../Models.cs#L39) `Generation` → PVE `bios=ovmf`+`efidisk0`,
[proxmox-integration.md gotcha 5](proxmox-integration.md#4-gotchas)); current power state.

**Cutover safety is load-bearing:** the source is left intact so rollback is always possible
([ARCHITECTURE.md §4](../ARCHITECTURE.md#4-migration-job-engine)). Decommissioning the source is a
separate, explicit, later operator action — not a migration step.

---

## 4. Conversion: virt-v2v wrapping

Decision 3 in practice. **Prefer `virt-v2v`** over a raw `qemu-img convert` because it does the
thing that makes a migrated *Windows* guest actually boot on KVM/QEMU: **injects virtio storage
and network drivers** and fixes the boot config. A plain format conversion produces a disk that
bluescreens on a virtio controller it has no driver for
([proxmox-integration.md gotcha 6](proxmox-integration.md#4-gotchas)).

- **Windows guest path:** `virt-v2v` (needs the virtio-win drivers available on the conversion
  host). Detect guest OS from inventory — Hyper-V already extracts it via KVP
  ([Scanner.cs:289](../../../Scanner.cs#L289) `GuestOs`).
- **Raw path:** `qemu-img convert` (VHDX → qcow2/raw) for cases where driver injection isn't
  needed (e.g. a Linux guest with virtio already, or disk-only moves). Linux-guest migration is a
  2.4 concern ([ROADMAP.md §2.4](../ROADMAP.md)).
- The engine **wraps** the tool: builds the argv, runs it through the SSH executor with a timeout,
  reads stdout/stderr concurrently (same anti-deadlock discipline as
  [Reachability.cs:179](../../../Reachability.cs#L179)), tails output as the step log (§6), and
  maps exit code → step success. It does **not** parse or reimplement conversion internals.

Where the tool runs (PVE node vs dedicated conversion host) is *Open question 1* and is the shared
ARCHITECTURE/agent/proxmox open question.

---

## 5. Edge cases the engine must handle

- **UEFI / Gen2 → OVMF:** provision `bios=ovmf` + `efidisk0`; without the EFI disk the VM won't
  boot. Precheck asserts it, Provision/Attach set it.
- **Secure Boot:** explicitly a 2.4 edge case ([ROADMAP.md §2.4](../ROADMAP.md)) — OVMF Secure
  Boot keys don't transfer; document as "migrate with Secure Boot off, re-enable post-migration"
  until handled. See *Open question 2*.
- **virtio vs IDE/SATA boot:** if drivers aren't injected, attach on an emulated bus (sata/ide)
  first, switch to virtio after the guest has the driver — `virt-v2v` normally removes the need,
  but the fallback path must exist.
- **Generation-1 / BIOS guests:** straightforward `seabios`, no EFI disk.
- **Multiple VHDs per VM:** Phase-1 inventory already models a list of disks
  ([Models.cs:44](../../../Models.cs#L44) `List<Vhd>`); Export/Convert/Attach fan out per disk and
  rejoin — each disk is its own idempotency unit.
- **Dynamic memory / generation-specific fields:** Hyper-V `DynamicMemory`
  ([Models.cs:43](../../../Models.cs#L43)) has no exact PVE equivalent — map to ballooning min/max
  and warn on imperfect fidelity rather than failing.

---

## 6. Per-step logging & progress

Each step writes **structured, persisted logs** ([persistence-and-security.md §1](persistence-and-security.md#1-schema-sketch))
— survives restart, available for post-mortem. Live progress streams over SSE by reusing
[EventHub.cs](../../../EventHub.cs) exactly as Phase-1 scan progress does
([Program.cs:339](../../../Program.cs#L339) `scanProgress`, [Program.cs:405](../../../Program.cs#L405)
`scanComplete`): provider `IProgress<T>`
([provider-abstraction.md §2](provider-abstraction.md#2-the-interface)) → Web → `Broadcast`. The
migration wizard ([ROADMAP.md §2.3](../ROADMAP.md)) consumes the same stream. Disk-copy and
`virt-v2v` progress arrive via the agent/SSH streaming paths
([agent-protocol.md §5](agent-protocol.md#5-streaming-progress)).

---

## 7. Stop, rollback, and idempotency

- **Idempotent steps.** Each step checks "is my output already present?" before acting (the
  Idempotency-key column in §3) so a resumed job re-running a step is a no-op, not a duplicate.
  This is what makes resume-after-crash safe.
- **Rollback hooks.** Each step declares a compensating action (§3 Rollback column). A hard stop
  or a step failure unwinds completed steps in reverse dependency order. Because cutover never
  deletes the source, full rollback always lands back on a working source VM.
- **Resumable.** Engine state lives in the DB, not memory — restart reloads and continues. This is
  the explicit ARCHITECTURE engine requirement: "each step idempotent and resumable, structured
  per-step logs persisted, hard stop + rollback hooks, dry-run mode"
  ([ARCHITECTURE.md §4](../ARCHITECTURE.md#4-migration-job-engine)).
- **Audit.** Every migration is a write action → recorded in the audit log
  ([persistence-and-security.md §5](persistence-and-security.md#5-audit-log),
  [ROADMAP.md cross-cutting](../ROADMAP.md#cross-cutting-every-milestone)).

---

## 8. Open questions (need a human decision)

1. **Conversion host placement.** `virt-v2v`/`qemu-img` on the PVE node (uses node resources, SSH
   on the hot path) vs a dedicated conversion container. Shared with
   [ARCHITECTURE.md open questions](../ARCHITECTURE.md#open-questions-for-the-spec-agents),
   [proxmox-integration.md Open question 2](proxmox-integration.md#5-open-questions-need-a-human-decision),
   and [agent-protocol.md Open question 2](agent-protocol.md#7-open-questions-need-a-human-decision).
   **Owner decision** — it shapes the staging topology.
2. **Secure Boot guests in MVP scope.** Confirm 2.3 MVP may *exclude* Secure-Boot Windows guests
   (precheck rejects them with a clear message) and defer to 2.4, rather than attempting key
   migration. **Owner decision.**
3. **Quiesce default (step 2).** Default to graceful shutdown (cold, consistent, downtime) or
   checkpoint/live-ish (warm, risk of in-flight data)? ARCHITECTURE says "configurable; warn on
   live-data risk" — confirm the **default** for the wizard.
