# VMentory Phase 2 — Roadmap

VMentory Phase 2 is a **multi-year platform**, not a 5-milestone phase: a **shared foundation**
plus **four product pillars** (ENG-0006), each of standalone value. Earlier framing as "dashboard +
management + one-way migration" was pillar 1 + half of pillar 2 — this roadmap reflects the full scope.

**Release model is incremental (ENG-0007).** There is **no big-bang v2**. Each milestone (2.0→2.4)
ships as **its own usable release** — continuous value, de-risked early. **v2.0 = planted Observe**
(the Phase-1 dashboard made persistent + multi-platform); the working v1 tool never sits frozen
waiting for the whole platform to land.

**Effort weighting is Proxmox-first (ENG-0007),** refining (not superseding) ENG-0006's conceptual
**Observe → Migrate → Deploy → Backup**. Under contended effort the practical ordering is
**Observe (plant) → Proxmox management/Deploy → HV→PVE migration**: the value is getting the estate
*onto* and *operating well on* Proxmox, with migration as the bridge. Hyper-V is the declining,
transitional platform — kept operable by **light management** (start/stop/reconfigure), never brought
to parity.

Milestones inside each pillar are ordered to **de-risk early**: the foundation and Hyper-V parity
first (prove the new shape doesn't regress Phase 1), then Proxmox read, then writes, then the
pillars. Each milestone is **independently shippable as a release** (ENG-0007).

> **Release-1 cut line (ENG-0007).** **PVE→HV reverse migration** (a nice-to-have) and
> **Backup/Restore** (needed eventually, but buy-vs-build undecided) are **explicitly deferred out of
> the first v2 release** — they arrive in later milestones (2.4 and 2.6 respectively), not in the
> initial line that gets the estate onto Proxmox.

> **The heavy agent foundation lands in 2.0 and is the gate for migration (ENG-0001).** Because
> there is **no winrun.py fallback** — the agent drives migration from day one — the agent runtime,
> enrollment, self-update, and internal CA must be built and proven in 2.0/2.2 *before* 2.3
> migration can start. If the agent slips, migration slips.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the target design and
[`docs/engineering/REGISTER.md`](../engineering/REGISTER.md) for the decisions (`ENG-NNNN`) each
milestone reflects.

---

# Shared foundation

## 2.0 — Foundation / **planted Observe** · ships as the **v2.0 release** (ENG-0007) · gate for everything
**Goal:** new architecture + the agent foundation, **Observe planted onto persistent multi-platform
storage**, zero behaviour regression vs Phase 1. **This is a shippable release** (ENG-0007): the
Phase-1 dashboard, now durable, is usable on its own before management or migration exist.

- [~] Rename `HyperInventory` → `VMentory.*`; split into `Core` / `Providers.*` / `Web` / `Agent` projects. **Slice 1 done:** `VMentory.sln` + `VMentory.Core` (domain) + `VMentory.Web` (exe) stood up, namespace renamed, zero behavior change. `Providers.HyperV` / `Providers.Proxmox` / `Agent` projects deferred to their own slices.
- [ ] Define `IVirtualizationProvider` + **capability model with management verbs gated per provider** (ENG-0007); generalize `Models.cs` into platform-neutral domain. **The capability model must allow management verbs (start/stop/reconfigure) on the Hyper-V provider — not source-only** — so 2.2 light HV management lands without reworking the abstraction (see [ARCHITECTURE.md → Capability model](ARCHITECTURE.md#capability-model--management-verbs-are-capability-gated-per-provider-eng-0007)).
- [ ] Build **VMentory.Agent** (ENG-0004): **NativeAOT single binary**, Windows Service (+ systemd unit), **constrained verb executor**. Reimplement `Scanner.cs` / `Reachability.cs` logic **natively in .NET** to run locally on the host (no winrun.py / Python, ENG-0001).
- [ ] Core↔Agent transport: **gRPC over HTTP/2 + mTLS** (ENG-0004); versioned verb contract (source-gen) + capability negotiation.
- [ ] **Agent enrollment + internal CA** (ENG-0003/0005): private CA in Core (root+intermediate, key in `ISecretStore`); manual install + single-use enrollment token → CSR → Core-signed client cert; allow/deny list (no CRL/OCSP).
- [ ] **Agent self-update** over the mTLS channel with watchdog rollback (ENG-0004), reusing the `Updater.cs` apply-on-launch pattern.
- [ ] `HyperVProvider` talks to the agent; dashboard shows the same data as Phase 1.
- [ ] Persistence layer (SQLite + EF Core): host registry + inventory snapshots. Migrate diff logic onto snapshots. **Single-operator schema — no `tenant_id`** (ENG-0006).
- [ ] **`ISecretStore`** + app-native envelope encryption (AES-256-GCM, runtime-injected KEK) (ENG-0002). Containerize Core. Replace session-token/loopback with a configured admin login.
- **Exit / release v2.0:** Core-in-container + enrolled agent-on-host reproduces the full Phase-1 Hyper-V dashboard **on persistent storage** (planted Observe — a usable standalone release, ENG-0007), and the agent can drive guest control + the migration step graph (the **2.3 gate**, ENG-0001).

# Pillar 1 — Observe

## 2.1 — Proxmox read (stats parity) · ships as a release
**Goal:** Proxmox hosts/VMs appear in the dashboard alongside Hyper-V.

- [ ] `ProxmoxProvider`: PVE REST client (scoped API token), map `/cluster/resources` + `status/current` + `rrddata` onto the domain.
- [ ] Multi-platform UI: provider switching / unified host list (direction A default, B grouped toggle), platform badges, capability-aware rendering.
- [ ] Historical stats view (now that snapshots persist).
- [ ] **Placement recommendations** groundwork — Observe is not passive; it feeds "where to move/deploy" into Migrate/Deploy (ENG-0006).
- **Exit:** one dashboard, both platforms, live + historical stats.

## 2.2 — Management (lifecycle) · completes the foundation · ships as a release
**Goal:** read/write verbs across both platforms; the agent gains its write/migration verbs.
**Proxmox is the full management target; Hyper-V gets light management** (ENG-0007).

- [ ] **Proxmox management** (the weighted-first management target, ENG-0007): full lifecycle via `ProxmoxProvider`.
- [ ] **Light Hyper-V management** (ENG-0007): the HV provider advertises **start / stop / reconfigure** (and snapshot/reset for transition operability) so the operator runs the whole estate from one tool while HV winds down — **not source-only, not full parity** (no Deploy/Backup on HV). Rides the same agent/provider/ops machinery; **gated by the capability model defined in 2.0**.
- [ ] Lifecycle ops (start / stop / shutdown / reset / snapshot) via provider abstraction, **capability-gated in the UI and the engine** — each verb shown/dispatched only where the provider advertises it.
- [ ] **Agent lifecycle + migration-control verbs** land here (ENG-0001/0004) — the second half of the 2.3 gate.
- [ ] *(Open: pin `reconfigure` scope on HV — vCPU/memory only vs disks/NICs/checkpoints — per [ENG-0007 open sub-question](../engineering/discussions/0007-product-strategy-release-scope.md#open-sub-questions). Keep it "light, not parity.")*
- [ ] Audit log for every write action (constrained-verb executions logged back to Core).
- [ ] Auth hardening (roles; OIDC optional).
- **Exit / release:** operate VMs on both platforms from VMentory (Proxmox full, Hyper-V light) with a full audit trail; agent can drive the migration step graph.

---

# Pillar 2 — Migrate

## 2.3 — Migration MVP (HV → PVE) · ships as a release
**Goal:** one-VM **Hyper-V → Proxmox** migration, end to end — the **release-1 migration direction**
(ENG-0007), serving the retire-Hyper-V North Star. **Bidirectional HV↔PVE is the eventual end state,
but PVE→HV reverse is out of release 1** (ENG-0007 / 2.4). Gated on the 2.0/2.2 agent foundation
(ENG-0001).

- [ ] **Operations engine** (generalized from the migration DAG, ENG-0006): persisted, resumable DAG; per-step logs; SSE progress; dry-run. Resumable across Core *and* agent restarts.
- [ ] Drive the **proven `qm importdisk` path on the PVE node** (ENG-0001): provision matching shell → import VHDX→raw → attach + boot/NIC/EFI wiring. **virt-v2v is an optional enhancement** for Windows virtio injection, not the baseline.
- [ ] Guest fix step (Linux netplan match-by-MAC; Windows virtio/SATA handling) — the embryo of the shared guest-customization layer (ENG-0006).
- [ ] **Quiesce by operator choice** per job, with caveats (ENG-0006) — not hardcoded.
- [ ] Migration wizard UI (precheck → map resources → run → watch → validate → cutover); the skill's **safe-verification rule** (prove source-off; boot copy isolated) baked in.
- [ ] Rollback / leave-source-intact safety.
- **Exit:** migrate a real VM HV→PVE through the wizard, boot it, validate.

## 2.4 — Migration scale & PVE→HV reverse · **deferred out of release 1** (ENG-0007)
**Goal:** beyond the HV→PVE happy path.

- [ ] Batch / queued migrations.
- [ ] Same-platform migration (HV↔HV, PVE↔PVE) via native APIs — a simpler subgraph of the same engine.
- [ ] **PVE→HV reverse migration** — a **distinct effort** (ENG-0006), **explicitly out of release 1** (ENG-0007): strip virtio, inject Hyper-V drivers, qcow2→VHDX, Gen2/UEFI. Counter to the retire-Hyper-V North Star, so it waits. *(Future ENG topic; not "the wizard reversed.")*
- [ ] UEFI / Secure Boot edge cases; failure-recovery UX, retry, partial-completion handling.

---

# Pillar 3 — Deploy

## 2.5 — Deploy MVP (provisioning)
**Goal:** create VMs from images/ISOs onto a chosen hypervisor, with guest customization.

- [ ] **Storage / repository layer** (new shared subsystem, ENG-0006): ISO library + golden-image / template repository. *Placement (Core volume vs storage host vs native) is an open ENG topic.*
- [ ] **Guest-customization layer** (new shared subsystem, ENG-0006, shared with Migrate): cloud-init / sysprep / unattend; network DHCP/static auto-config; **post-deploy app install** (e.g. Atera with operator-supplied package). *Engine choice is an open ENG topic.*
- [ ] Deploy verbs on the agent + `ProxmoxProvider` (create-from-template / install-from-ISO), capability-gated.
- [ ] Creation wizard UI; target-hypervisor choice; consumes Observe placement recommendations.
- **Exit:** provision a customized VM from a template or ISO onto either platform through the wizard.

---

# Pillar 4 — Backup / Restore

## 2.6 — Backup / Restore · **deferred out of release 1** (ENG-0007)
**Goal:** scheduled backup + restore to either platform. **Out of the first v2 release** (ENG-0007), and **buy-vs-build is deferred** (ENG-0006) — a stated goal with the approach explicitly **undecided**; do not assume an implementation here.

- [ ] **Buy-vs-build decision** (future ENG topic): orchestrate **Proxmox Backup Server / Veeam** vs build a **native backup engine** (dedup/incremental/verify/replication). Arguably bigger than the other three pillars combined; decided when 1–3 are further along.
- [ ] **Scheduling** subsystem (new, ENG-0006): recurring jobs on the operations engine, not just on-demand.
- [ ] **Data-movement-at-scale** (new, ENG-0006): dedup, incrementals, off-site-over-WAN replication.
- [ ] Backup + restore to **either** platform; restore-testability; reports; storage management (on the repository layer).
- **Exit:** scheduled, verifiable backup with a tested restore to either platform.

---

## Cross-cutting (every milestone)
- **Docs** kept current by the documentation agent (`docs/phase2/specs/`); decisions in `docs/engineering/`.
- **UI** explored by the UI design agent (`design/` workflow) before implementation.
- **No regression** in the security posture: secrets never at rest in plaintext (ENG-0002); agent is a constrained verb executor over mTLS (ENG-0004); audit everything that writes.
