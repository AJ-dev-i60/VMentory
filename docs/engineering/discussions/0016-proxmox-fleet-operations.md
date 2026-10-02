# ENG-0016 — Proxmox fleet operations: capacity, ranking, move, drain

**Status:** Decided (2026-10-02) · **BUILT 2026-10-02** (read path verified live on vega14; write path
built, not yet exercised — see *Not yet proven*) · **Owner:** human
**Relates to:** ENG-0009 (Proxmox transport — REST + constrained SSH), ENG-0015 (estate, actions,
OME join), ENG-0008 (RBAC), ENG-0001 (the *other* migration pillar, HV→PVE — unchanged)

## Trigger

The owner's "v3" brief (Outline: *VMentory v3 — Proxmox estate operations*, 2026-09-25):

> I want a centralized page to view and manage all my proxmox hosts, with capacity, storage,
> ranking... I want it to be able to shift a vm to another host (the target list sorted by highest
> suggestion to lowest, but able to select any host). I also want to be able to clear a particular
> host for maintenance.

Plus a hard rule given when the build was commissioned (2026-09-25):

> It lists a lot of real values like ram and disk usage, I want those to only be examples, this
> system must ALWAYS pull only real values. If you don't KNOW, don't PRETEND. Rather put blanks or
> placeholders, but ALWAYS pull real data or calculate when presenting numbers.

## Scope change this records

The roadmap's migration pillar is **Hyper-V → Proxmox** (ENG-0001). This is **Proxmox → Proxmox**
between five *standalone* nodes, and it pulls the write-verb slice (demoted 2026-08-03) forward for
exactly two verbs: *move a guest* and *drain a host*. Nothing else is managed (no start/stop,
reconfigure, snapshot) — those stay behind the capability flags.

## Decision

### The number rule, made structural
Every reading type (`VMentory.Core/Fleet/FleetModels.cs`) uses nullable fields: a value a node did
not report is `null`, never `0` and never a default. The API carries the reason beside each null; the
UI renders `—` with that reason. Concretely:

* A node that stops answering keeps its **last good reading, served with its age** and flagged stale
  — never zeros.
* The **score** is computed only over inputs that were read. Missing inputs are dropped, the
  remaining weights renormalised, and the card says *"scored without: hardware"*. The brief's worked
  example scores were not used anywhere.
* **ETA** = bytes ÷ the transfer rate **measured on this deployment's own last 10 successful moves**.
  Until one move has completed there is no rate, and the ETA is blank. The brief's 100–109 MB/s is
  *not* used as a default.
* Thresholds (memory floor 16 GiB / 10%, pool keeps 10% free, score weights) are **policy**, served
  by `/api/fleet` as policy and shown as such — they are not data.
* The Estate page's backup tile used a hand-written "of ~50" denominator; replaced with the live
  guest count and a label saying the backup list is hand-recorded.

### Read path: REST with a read-only token, plus a forced-command probe for what REST will not give
One **PVEAuditor** token per node (`vmentory@pve!monitor`, the vega14 pattern ×5), polled every 60 s,
all nodes in parallel; one node's failure never blocks the others.

Verified live on vega14 (2026-10-02): the audit role **cannot** read
* per-volume usage — `GET /nodes/{n}/storage/{s}/content/{vol}` and the content listing need
  `VM.Config.Disk`, a write privilege;
* storage config — `GET /storage/{id}` needs `Datastore.Allocate`;
* the ZFS ARC cap — not in any endpoint (`rrddata` has the *current* ARC size only);
* the DMI serial (= Dell service tag) — not exposed.

Granting write privileges to the poller was rejected. Instead, per ENG-0009's "constrained SSH for the
residue", a dedicated key is installed with `restrict,command="/usr/local/sbin/vmentory-probe"`. The
probe is read-only and prints: ARC `c_max`, DMI serial/product, `storage.cfg` facts (pool, vgname,
path, sparse), and **allocated bytes per volume** (ZFS `logicalreferenced` — uncompressed, what a
copy transfers; LVM thin `size × data_percent`, thick = size; `du` for dir-store image files).
`ops/provision-fleet-nodes.sh` installs it. Without the probe those values show as unknown, which is
correct.

### Capacity: three numbers, never one
Physical (`MemTotal`), **allocated** (Σ configured memory of *running* guests + ARC cap), **available**
(`MemAvailable`) — one bar, three labels; ARC current size shown beside. CPU: vCPU committed : threads
with load beside it; LXCs with no core limit are counted separately, not folded in. Storage: size,
used, free, and the thin ratio = Σ configured guest-disk sizes on the store ÷ free (from guest
configs, since the audit role cannot list a store; orphan volumes are therefore not in it — stated).

### Score & vetoes
As the brief specified (mem .40, cpu .20, storage .20, hardware .10, load .10; vetoes for
maintenance/unreachable/stale, memory floor, no store with room, anti-affinity, hardware Critical).
Guest-specific ranking scores each target *after* the move. Vetoed targets stay **visible and
selectable**; choosing one turns the vetoes into warnings the operator must acknowledge.

### Moves
`POST /nodes/{src}/{qemu|lxc}/{vmid}/remote_migrate` with the source's write token; the target
endpoint carries the target's write token and its **live-read** certificate fingerprint. Tracked by
UPID; task log tail stored; cancel = stop the task.

* **Live** only when both nodes report the **identical CPU model string** (CPU groups are derived from
  the readings, not hard-coded). Otherwise a running VM is **shut down cleanly** (ACPI, 180 s,
  **never forced**), copied, then started on the target. LXC always uses restart mode.
* `delete=0` always: Proxmox leaves the source copy **stopped with `lock: migrate`** (verified in
  `QemuMigrate.pm` — phase1 sets the lock and only the error cleanups clear it), so it cannot be
  started by accident. That is the "never two runnable copies" guarantee for pinned guests like
  `arcserveprimary`, and the rollback. The job verifies and records the source lock after success.
* On failure: anything the job created on the target is recorded (only when the VMID was free
  before), offered for cleanup, and the guest is started again on the source if the job stopped it.
* **One move at a time, fleet-wide**, enforced by the runner — stricter than "per node pair" and free
  on a single 1 Gbps network. A re-preflight runs at execution time.
* Dry run is the default; execution needs the exact phrase `<guest> to <node>`, plus an explicit
  acknowledgement when there are warnings.

### Drains
Plan → cost → approve (phrase `drain <node>`, maintenance reason + expiry required) → sequential
execution → abort (after current / now) → report with a recorded reverse plan. Ordering: running
`onboot=0` guests first, `SelfHost`-rule guests (VMentory's own VM) last, stopped guests stay unless
flipped. Placement simulates the plan's own load on each target. While a node is in maintenance it
leaves every target list and its OME faults stop raising actions until expiry. A drain can name the
remediation action it serves; it notes and progresses that action. **Return to service** creates a
new draft for human approval — never automatic.

### Rules
`FleetRuleEntity` — AntiAffinity, Pinned, Ephemeral, SelfHost — by guest *name* (VMIDs change on a
move). Seeded once from the brief's stated facts (iSixty DCs, i60 DCs, Arcserve primary,
gitlab-restore-test, the Coolify VM hosting VMentory), editable, labelled `seed`. Rule members not
present in the fleet are reported, not assumed.

### Permissions, audit
New `ConsolePermission.MigrateGuest` and `DrainHost`, Admin only. Every request, start, copy start,
outcome, cancel, cleanup, maintenance change, drain approval/abort and rule edit writes an
`AuditEventEntity` (`fleet.*`) with guest, source, target, mode, bytes, duration, outcome.

## Owner decisions taken as defaults (brief §"Decisions the owner still owes")
1. **Cluster?** Stay standalone — the design assumes it. Revisit is cheap: a cluster would make most
   of the write path native.
2. **Standardise CPU model?** No. Live is offered only within a CPU model group (today vega14 ↔
   sirius16).
3. **Online at all?** Offered where it can work; offline is always available and is the default for
   pinned guests.
4. **Automatic return to service?** No — a reverse *draft*, approved by a human.
5. **How far does "manage" go?** Move and drain only.

## Not yet proven (as of 2026-10-02)
* Provisioning of the four other nodes (tokens, role `VMentoryMigrate`, probe key) — the session's
  permission classifier refused it; the owner runs `ops/provision-fleet-nodes.sh`.
* The write path end to end, including whether `VMentoryMigrate`'s privilege list is sufficient for
  `remote_migrate`/`mtunnel` with a non-root token. First test should be a throwaway empty VM.
* Probe output parsing against real nodes (parsers tested on sample input only).

## Out of scope
Metrics/time series, HA, auto-balancing, scheduled moves, Hyper-V, backup orchestration, the Arcserve
schedule feed (preflight states it is not connected).
