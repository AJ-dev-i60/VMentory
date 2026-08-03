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

> ### ⚠ Re-focused 2026-08-03 — monitoring-first, both platforms (ENG-0007 amendment + ENG-0013)
>
> **VMentory's primary focus is now a monitoring tool for Hyper-V *and* Proxmox hosts.** Observe stops
> being *the plant beneath the pillars* and becomes **the product**, taken to depth on both platforms
> before management, Deploy or Migrate get further effort. The practical ordering above becomes
> **Observe (both platforms, to depth) → Proxmox management/Deploy → HV→PVE migration.**
>
> - **Hyper-V reaches monitoring parity — and only monitoring parity.** "Light management, not parity"
>   still governs *management verbs*; it does not govern Observe, because a monitoring tool blind on one
>   of its two platforms is not the product. Hyper-V still **declines** — the North Star is unchanged.
> - **The blocker this exposes (ENG-0013):** Hyper-V is **wholly unreachable from the containerized
>   Core** — `Reachability.cs:169` shells `powershell.exe`, `:20` shells `ping.exe`, and `Scanner.cs:216`
>   assumes Core is the domain WinRM *client*. Only the bare TCP probe at `Program.cs:517` survives on
>   Linux. This is the root cause of the ENG-0011 trigger. **Resolved by SSH + PowerShell on the host**,
>   through **one shared `ISshExecutor`** that also serves the ENG-0009 Proxmox residue.
> - **Slice 5 below (Proxmox SSH executor + write verbs) is demoted** behind the monitoring slices; its
>   SSH-executor half is pulled *forward* and generalised to both platforms. See the re-sequenced order
>   under **Approved foundation slice order**.
> - **Scope bound:** health + inventory, **no metrics**. No time-series store, no trend charts, no
>   thresholds or alerting — those remain unraised and unbudgeted, and would need their own ENG topic.

**Foundation sequencing was re-baselined and approved 2026-06-16 (ENG-0009/0010, amended
0001/0003/0004/0005).** The product pillars and their ENG-0006/0007 weighting are **unchanged**; what
changed is the **order of the foundation work beneath them**. Two corrections drive it:
- **Proxmox needs no agent (ENG-0009).** It is driven by its **native REST API (the primary control
  plane)** plus a **constrained, forced-command SSH key** for the disk-import / conversion / on-node
  guest-edit residue. The on-device agent + private-CA mTLS foundation (ENG-0001/0003/0004/0005) is
  therefore **demoted and scoped to the Hyper-V migration source only**, and **pushed off the 2.0
  critical path** to the migration slice — it is no longer the front-loaded foundation.
- **Core is a hosted container, web-first, install-nothing (ENG-0010).** The first foundation slice is
  standing the container up as a real hosted service (0.0.0.0 bind, Core-terminated HTTPS), with **login
  + RBAC** (ENG-0008) landing early — before any write verb — not the agent.

**Approved foundation slice order (re-baseline):**
1. **Containerized Core + hosted-service bootstrap** (ENG-0010) — replace the loopback/random-port/
   session-token desktop bootstrap with a configurable `0.0.0.0` bind and Core-terminated HTTPS.
2. **Login + RBAC framework** (ENG-0008) — minimal admin login replaces the session token; fixed roles
   + pillar×verb catalog + the single audited authz chokepoint, before any write verb.
3. **`ISecretStore`** (ENG-0002) — app-native envelope encryption, runtime-injected KEK; lets creds/
   tokens persist.
4. **Proxmox API provider** (read / planted Observe) — PVE REST client, Proxmox hosts/VMs in the dashboard.
5. ~~**Proxmox SSH executor + management/Deploy write verbs**~~ — **demoted 2026-08-03** behind the
   monitoring slices (ENG-0007 amendment). Its SSH-executor half is pulled forward into slice 7 below
   and generalised to both platforms; the write verbs re-enter as slice 9.
6. **Hyper-V agent + private CA** (ENG-0001/0003/0004/0005, scoped to HV) — the demoted agent foundation.
7. **HV→PVE migration** (ENG-0001/0006).

**Re-sequenced from here (2026-08-03, monitoring-first — ENG-0007 amendment + ENG-0013):**

5. **Named credentials** (ENG-0012) — `CredentialEntity` + typed `Host` slots, global creds retired.
   **Strictly first:** slice 7 gives Hyper-V an SSH key, so both platforms are about to grow a second
   per-host secret; the untyped model must not be the thing carrying them.
6. **Health model** (ENG-0011a) — typed `HealthTier`/`FailureStage`/`HostFault`/`HostHealth` in
   `VMentory.Core`, `IVirtualizationProvider` returning a per-stage fault **list**, one evaluator
   unifying add-host + poller, `health` JSON over `/api/state`+SSE replacing the `Reachability`
   booleans. This is the backbone of monitoring on both platforms.
7. **Hyper-V over SSH + shared `ISshExecutor`** (ENG-0013) — one executor seam in Core serving HV
   inventory/health *and* the Proxmox on-node residue. `BuildRemoteWrapper`/`Invoke-Command`,
   `RunPowerShellAsync`, the local WinRM ensure and `TrustedHosts` are deleted, not ported.
   **Acceptance gate: an end-to-end read from a real Hyper-V host**, as vega14 proved the PVE path.
8. **Multi-platform monitoring dashboard** — the pending design pick (unified list vs platform-grouped,
   `design/STATUS.md`) rendered against the six-tier health language + fault drill-down.
9. **Proxmox management/Deploy write verbs** — the demoted half of the old slice 5; capability-gated,
   RBAC-enforced, audited. First real writes through the ENG-0008 chokepoint.
10. **Hyper-V agent + private CA**, then **HV→PVE migration** — unchanged in content, but now justified
    **solely** by migration (ENG-0013 removed the agent from the monitoring path, as ENG-0009 did for
    Proxmox).

Slices 1–3 of the *original* build already landed (project split, provider abstraction, persistence —
see §2.0 below); the re-baseline re-orders **what comes next**, starting at containerized Core.

> **Release-1 cut line (ENG-0007).** **PVE→HV reverse migration** (a nice-to-have) and
> **Backup/Restore** (needed eventually, but buy-vs-build undecided) are **explicitly deferred out of
> the first v2 release** — they arrive in later milestones (2.4 and 2.6 respectively), not in the
> initial line that gets the estate onto Proxmox.

> **The agent foundation is demoted and scoped to Hyper-V (ENG-0009).** It is **no longer the
> front-loaded 2.0 foundation**. Because there is still **no winrun.py fallback** — the agent drives
> the *Hyper-V* migration source — the agent runtime, enrollment, self-update, and the private CA must
> be built and proven **before 2.3 migration**, but they are now sequenced **with the migration slice**
> (foundation slice 6), not ahead of Observe/Proxmox. Proxmox uses **no agent** (ENG-0009).

See [ARCHITECTURE.md](ARCHITECTURE.md) for the target design and
[`docs/engineering/REGISTER.md`](../engineering/REGISTER.md) for the decisions (`ENG-NNNN`) each
milestone reflects.

---

# Shared foundation

## 2.0 — Foundation / **planted Observe** · ships as the **v2.0 release** (ENG-0007) · gate for everything
**Goal:** new architecture + the **hosted-container foundation**, **Observe planted onto persistent
multi-platform storage**, zero behaviour regression vs Phase 1. **This is a shippable release**
(ENG-0007): the Phase-1 dashboard, now durable and served as a hosted web console, is usable on its own
before management or migration exist. **The agent/CA foundation is NOT here** — it is demoted to the
Hyper-V migration source and sequenced with 2.3 (ENG-0009).

**Already landed (original build slices 1–3 — verified on `dev`):**
- [x] Rename `HyperInventory` → `VMentory.*`; split into `Core` / `Web` projects. `VMentory.sln` + `VMentory.Core` (domain) + `VMentory.Web` (exe); namespace renamed, zero behavior change. `Providers.*` / `Agent` projects deferred to their own slices.
- [x] `IVirtualizationProvider` + **capability model with management verbs gated per provider** (ENG-0007). Full `ProviderCapability` flags in `VMentory.Core`; `Host.Platform` discriminator; `HyperVProvider` (in `VMentory.Web`) advertises `Inventory|LiveStats` and the live HV inventory reads flow through the seam. The model **must allow management verbs (start/stop/reconfigure) on the Hyper-V provider — not source-only** (see [ARCHITECTURE.md → Capability model](ARCHITECTURE.md#capability-model--management-verbs-are-capability-gated-per-provider-eng-0007)). *Pending:* lifecycle/migration **verb methods** + advertisement, `ProxmoxProvider`.
- [x] Persistence layer (SQLite + EF Core): host registry + inventory snapshots; diff fed from persisted snapshots; always-on in real mode, `--mock` ephemeral; `VMENTORY_DB` env override; `/api/quit` no longer purges. **Single-operator schema — no `tenant_id`** (ENG-0006). *Pending:* snapshot retention/cadence policy (open Q), Postgres option. *(Secrets persisted via `ISecretStore` in re-baselined slice (3).)*

**Re-baselined foundation slices (approved 2026-06-16; this is the order they ship in):**
- [x] **(1) Containerized Core + hosted-service bootstrap** (ENG-0010) — **built & verified on `dev` 2026-06-17, deployed 2026-06-18.** `Program.cs` rewritten loopback-desktop → hosted service: binds **`VMENTORY_HTTP_ADDR` (default `0.0.0.0`) : `VMENTORY_HTTP_PORT`** via `ConfigureKestrel`, dropping `127.0.0.1:{random}` + `FindFreePort()` (and `OpenBrowser`/R-key; console Q-loop now TTY-gated on `!Console.IsInputRedirected`; WinRM ensure `IsWindows()`-guarded). **Core-terminated HTTPS** via `listen.UseHttps` with new `TlsSetup.cs` (cert precedence **operator PFX → operator PEM → self-signed**, real-mode cached / mock ephemeral, nothing baked in); **`VMENTORY_HTTP_ONLY=1`** disables TLS for reverse-proxy/dev. Single multi-stage Linux `Dockerfile` + `.dockerignore` (app + `openssh-client`; **no `qemu`/`qm`**; **non-root uid 10001**; DB + cached cert on the **`/data` volume**; `EXPOSE 8443`); csproj manifest Windows-only-conditioned. Deployed as a Coolify dev instance at https://vmentorydev.edgestudios.co.za. *Pending:* `/api/quit` desktop route retirement; `Updater.cs` re-scope (GitHub-exe → image tags).
- [x] **(2) Login + RBAC framework** (ENG-0008) — **built & verified 2026-06-18.** Cookie-based session auth (`vmentory_session`, HttpOnly Secure SameSite=Strict, 12h sliding); `PasswordHasher` (PBKDF2-SHA256, BCL-only, work-factor in hash); EF Core `AppUserEntity` + `AuditEventEntity` (`AddAuth` migration); `IUserStore`/`EfUserStore`; `RbacCatalog` (static role→`ProviderCapability`+`ConsolePermission` mapping). Fixed roles: Admin / VmOperator / BackupOperator / Viewer. Single audited authz chokepoint middleware gates all `/api/*`; passes `/api/auth/*`; enforces `MustChangePassword`. First-admin seed on startup (`VMENTORY_ADMIN_USER`/`VMENTORY_ADMIN_PASSWORD`). Session token **retired**; SSE now uses cookie auth. *Pending:* login UI awaiting design (request raised); `/api/quit` + Updater re-scope still pending.
- [x] **(3) `ISecretStore`** + app-native envelope encryption (AES-256-GCM, runtime-injected KEK) (ENG-0002) — **built & verified 2026-06-18.** `AesGcmSecretStore` (DB-backed, scoped, AES-256-GCM); `EphemeralSecretStore` (singleton, in-memory fallback); `DekProvider` (KEK/DEK manager, cached in-memory). `SecretEntity`/`DekEntity` + `AddSecrets` migration. `VMENTORY_KEK` env var gates persistence. Credentials (global WinRM + per-host) persist via `ISecretStore`; restored at startup. *Pending:* login UI awaiting design; `/api/quit` + Updater re-scope still pending.
- [x] **(4) Proxmox API provider — read / planted Observe** — **built 2026-06-18, commit `2907fd4`.** `ProxmoxProvider` PVE REST client (scoped API token via `ISecretStore`, `PVEAPIToken` header); calls `/api2/json/version`, `/nodes`, `/nodes/{n}/status`, `/nodes/{n}/qemu`, `/nodes/{n}/lxc`; advertises `Inventory | LiveStats`. `ProviderRegistry` routes by `PlatformKind`. `Host` + persistence entities gained `SkipTlsVerification`; `AddProxmox` migration. `Poller` and `Program.cs` updated for platform-aware routing. SPA add-host modal: platform selector, conditional HV-creds vs PVE-token fields; host rows: HV/PVE badge. **One Host = one PVE node** (multi-node cluster support deferred).
- [ ] **(5) Proxmox SSH executor + management/Deploy write verbs** ← **NEXT**: constrained SSH executor (ENG-0009); first write verbs (`start`/`stop`/`shutdown`/`snapshot`) via `ProxmoxProvider`; capability-gated in the UI; RBAC-enforced by the existing authz chokepoint (these are the first real write verbs — they are the reason ENG-0008 had to exist early).
- **Exit / release v2.0:** Core-in-container, served over HTTPS on a real bind with **login + RBAC**, reproduces the full Phase-1 Hyper-V dashboard **on persistent storage** and shows Proxmox read-side too (planted, multi-platform Observe — a usable standalone release, ENG-0007). Slice (4) is the read-side completion of v2.0 Observe. **No agent and no migration here** (ENG-0009): the agent/CA + HV→PVE migration arrive in foundation slices 6–7 with 2.3.

# Pillar 1 — Observe

## 2.1 — Proxmox read (stats parity) · ships as a release
**Goal:** Proxmox hosts and VMs appear alongside Hyper-V with full stats parity. `ProxmoxProvider`
(landed in foundation slice 4, 2026-06-18) already delivers the read inventory; this milestone
completes the stats layer and the multi-platform UI on top of it.

- [x] `ProxmoxProvider` core: PVE REST client (scoped API token), `/nodes`, `/nodes/{n}/status`, `/nodes/{n}/qemu`, `/nodes/{n}/lxc` — **shipped in 2.0 foundation slice (4), commit `2907fd4`**.
- [ ] `rrddata` polling for live CPU/memory time-series stats (the `LiveStats` capability is advertised; the `rrddata` call is the remaining piece).
- [ ] Multi-platform UI: unified host list polish (direction A default, B grouped toggle), capability-aware rendering (show/hide verbs by provider capability), per-platform resource summary.
- [ ] Historical stats view (snapshots persist since original slice 3; needs the UI read path).
- [ ] **Placement recommendations** groundwork — Observe is not passive; it feeds "where to move/deploy" into Migrate/Deploy (ENG-0006).
- **Exit:** one dashboard, both platforms, live + historical stats.

## 2.2 — Management (lifecycle) · completes the foundation · ships as a release
**Goal:** **Proxmox** read/write verbs (foundation slice 5 — Proxmox SSH executor + management/Deploy
write verbs, capability-gated, RBAC-enforced, audited). **Proxmox is the full management target;
Hyper-V gets light management** (ENG-0007) — but HV light management rides the **agent**, so it lands
**with the agent slice (6)/2.3-era**, not here, since Proxmox needs no agent (ENG-0009).

- [ ] **Proxmox management** (the weighted-first management target, ENG-0007): full lifecycle via `ProxmoxProvider` — REST API for lifecycle/config/native PVE↔PVE migration + the **constrained SSH executor** for the on-node residue (ENG-0009).
- [ ] Lifecycle ops (start / stop / shutdown / reset / snapshot) via provider abstraction, **capability-gated in the UI and the engine** — each verb shown/dispatched only where the provider advertises it.
- [ ] **RBAC enforcement on the write verbs** (ENG-0008): the role framework + authz chokepoint shipped in the 2.0 deployment slice now **gates these first write verbs** — they are the reason the framework had to exist early.
- [ ] Audit log for every write action (each provider action logged back to Core).
- [ ] **Light Hyper-V management** (ENG-0007) — **deferred to the agent slice (6)/2.3-era** because it rides the agent (ENG-0009): the HV provider advertises **start / stop / reconfigure** (+ snapshot/reset for transition operability) so the operator runs the whole estate from one tool while HV winds down — **not source-only, not full parity** (no Deploy/Backup on HV). Gated by the same capability model (slice 2 of the original build) + RBAC.
- [ ] *(Open: pin `reconfigure` scope on HV — vCPU/memory only vs disks/NICs/checkpoints — per [ENG-0007 open sub-question](../engineering/discussions/0007-product-strategy-release-scope.md#open-sub-questions). Keep it "light, not parity.")*
- **Exit / release:** operate Proxmox VMs from VMentory (full lifecycle) with RBAC + a full audit trail; HV light management + the agent that carries it arrive with 2.3.

---

# Pillar 2 — Migrate

## 2.3 — Migration MVP (HV → PVE) · ships as a release
**Goal:** one-VM **Hyper-V → Proxmox** migration, end to end — the **release-1 migration direction**
(ENG-0007), serving the retire-Hyper-V North Star. **Bidirectional HV↔PVE is the eventual end state,
but PVE→HV reverse is out of release 1** (ENG-0007 / 2.4).

- [ ] **Hyper-V agent + private CA** (foundation slice 6 — ENG-0001/0003/0004/0005, **scoped to the HV migration source** per ENG-0009): NativeAOT single binary as a Windows Service, **constrained verb executor** reimplementing `Scanner`/`Reachability` logic natively in .NET; gRPC/HTTP2 + **mTLS**; private CA inside Core (root+intermediate, key in `ISecretStore`); manual install + single-use enrollment token → CSR → Core-signed cert; self-update over the mTLS channel with watchdog rollback. **This is the demoted agent foundation — it lands here, with migration, not in 2.0.** It also carries the **light HV management** verbs (2.2). No winrun.py fallback (ENG-0001), so this is the gate for the rest of 2.3.
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
- [ ] Deploy verbs on `ProxmoxProvider` (create-from-template / install-from-ISO via PVE REST + the SSH executor), capability-gated. **Proxmox-side only** — HV deliberately does not advertise `Provision` (ENG-0007 "light, not parity"; ENG-0009 no PVE agent).
- [ ] Creation wizard UI; target-hypervisor choice; consumes Observe placement recommendations.
- **Exit:** provision a customized VM from a template or ISO onto Proxmox through the wizard.

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
- **No regression** in the security posture: secrets never at rest in plaintext (ENG-0002); Core-terminated HTTPS for the web console + **login/RBAC before any write verb** (ENG-0008/0010); Proxmox driven by scoped API token + constrained SSH key, no node agent (ENG-0009); the **Hyper-V** agent (when it lands in 2.3) is a constrained verb executor over mTLS (ENG-0004); audit everything that writes.
