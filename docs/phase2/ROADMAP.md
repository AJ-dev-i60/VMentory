# VMentory Phase 2 — Roadmap

Milestones are ordered to **de-risk early**: re-architecture and Hyper-V parity first
(prove the new shape doesn't regress Phase 1), then Proxmox read, then writes, then migration.
Each milestone should be independently shippable.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the target design these build toward.

---

## 2.0 — Foundation (no new user-facing features)
**Goal:** new architecture, zero behaviour regression vs Phase 1.

- [ ] Rename `HyperInventory` → `VMentory.*`; split into `Core` / `Providers.*` / `Web` / `Agent` projects.
- [ ] Define `IVirtualizationProvider` + capability model; generalize `Models.cs` into platform-neutral domain.
- [ ] Build **VMentory.Agent** (Windows service): relocate `Scanner.cs` / `Reachability.cs` logic to run locally on the host. Define Core↔Agent transport + auth (mTLS/token).
- [ ] `HyperVProvider` talks to the agent; dashboard shows the same data as Phase 1.
- [ ] Persistence layer (SQLite + EF Core): host registry + inventory snapshots. Migrate diff logic onto snapshots.
- [ ] Containerize Core; secrets via Docker secrets/env. Replace session-token/loopback with a configured admin login.
- **Exit:** Core-in-container + agent-on-host reproduces the full Phase-1 Hyper-V dashboard.

## 2.1 — Proxmox read (stats parity)
**Goal:** Proxmox hosts/VMs appear in the dashboard alongside Hyper-V.

- [ ] `ProxmoxProvider`: PVE REST client (API token), map `/cluster/resources` + `status/current` + `rrddata` onto the domain.
- [ ] Multi-platform UI: provider switching / unified host list, platform badges, capability-aware rendering.
- [ ] Historical stats view (now that snapshots persist).
- **Exit:** one dashboard, both platforms, live + historical stats.

## 2.2 — Management (lifecycle)
**Goal:** read/write verbs across both platforms.

- [ ] Lifecycle ops (start / stop / shutdown / reset / snapshot) via provider abstraction, capability-gated in the UI.
- [ ] Audit log for every write action.
- [ ] Auth hardening (roles; OIDC optional).
- **Exit:** operate VMs on both platforms from VMentory with a full audit trail.

## 2.3 — Migration MVP
**Goal:** one-VM **Hyper-V → Proxmox** migration, end to end.

- [ ] Migration job engine: persisted, resumable DAG; per-step logs; SSE progress; dry-run.
- [ ] Orchestrate `virt-v2v` (Windows guest virtio injection) + PVE provisioning + `qm importdisk`.
- [ ] Migration wizard UI (precheck → map resources → run → watch → validate → cutover).
- [ ] Rollback / leave-source-intact safety.
- **Exit:** migrate a real Windows VM HV→PVE through the wizard, boot it, validate.

## 2.4 — Scale & polish
**Goal:** beyond the happy path.

- [ ] Batch / queued migrations; scheduling.
- [ ] Same-platform live migration (HV↔HV, PVE↔PVE) via native APIs.
- [ ] Linux-guest migration path; UEFI/Secure Boot edge cases.
- [ ] Failure-recovery UX, retry, partial-completion handling.

---

## Cross-cutting (every milestone)
- **Docs** kept current by the documentation agent (`docs/phase2/specs/`).
- **UI** explored by the UI design agent (`design/` workflow) before implementation.
- **No regression** in the security posture: secrets never at rest in plaintext; audit everything that writes.
