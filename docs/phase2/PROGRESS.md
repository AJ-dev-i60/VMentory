# VMentory Phase 2 — Progress & Resumption

> **Purpose:** pick up Phase 2 from a clean clone on any machine. Read this top-to-bottom and you
> know where we are, what's decided, what's open, and what to do next.
>
> **Last updated:** 2026-06-16 · **Phase:** 2.0 foundation — implementation started (slices 1–3 landed) · **Branch:** `dev`

---

## 1. Orientation (60 seconds)

VMentory Phase 1 = single-exe, **Windows-only, read-only, ephemeral** Hyper-V inventory tool
(ASP.NET Core 8 + vanilla-JS SPA, namespace `HyperInventory`, in-memory only, WinRM via
`powershell.exe`). It works and ships today.

Phase 2 = turn it into a **container-based, single-operator, multi-platform (Hyper-V + Proxmox)
platform** with Windows + Linux on-device agents — a Linux-container "Core" that talks to providers,
persists state, and runs operations.

**North Star (ENG-0007, 2026-06-16):** move **entirely off Hyper-V onto Proxmox**. VMentory v2 is a
**Proxmox-first management super-tool**; Hyper-V's role **declines over time** and is primarily a
**migration source**. v1 "Observe" is being **"planted"** — made persistent + multi-platform — as the
**foundation** for everything else (not a fifth pillar).

**Product scope (ENG-0006, 2026-06-16): four pillars on one shared foundation**, each standalone
value. ENG-0007 **refines** ENG-0006 (does not supersede it): the four pillars are no longer equal —
they are **weighted and sequenced** toward the Proxmox-first North Star:
**Observe (plant) → Proxmox management/Deploy → HV→PVE migration**.
1. **Observe (plant)** — resource/load dashboard (Phase-1 root), made persistent + multi-platform; the foundation, also feeds placement recommendations.
2. **Proxmox management / Deploy** — Proxmox-first management + VM provisioning: ISO/image repo, creation wizard, guest customization, post-deploy apps.
3. **Migrate** — wizard-driven **HV→PVE** (the priority direction); **bidirectional HV↔PVE** remains the eventual end state, but **PVE→HV is deferred** (see §2).
4. **Backup/Restore** — to either platform, dedup, off-site replication (**buy-vs-build deferred**; **out of release 1**, see §2).

New shared subsystems the vision adds (⚠ not yet in ARCHITECTURE/ROADMAP — documentation agent to
expand): storage/repository layer, guest-customization layer (shared Deploy+Migrate), scheduling,
data-movement-at-scale; the migration job engine generalizes to a **general operations engine**.

> ✅ ENG-0006/ENG-0007 are **propagated** into ARCHITECTURE.md and ROADMAP.md (commits `8c216ae` /
> `087b260`): incremental release definitions/sequencing and the **provider capability model with
> Hyper-V management verbs allowed** are in place. The remaining held propagation is the
> virt-v2v→`qm importdisk` reconciliation in `migration-job-model.md` (see §4).

The target design is in [ARCHITECTURE.md](ARCHITECTURE.md); the milestone plan is in
[ROADMAP.md](ROADMAP.md). **Phase 2 implementation has started** at the 2.0 foundation — slices 1
(project split / rename) and 2 (provider abstraction + capability model) are landed and verified on
`dev` (see §3 and §5); persistence, the agent, and `ISecretStore` are next.

## 2. Locked decisions (the spine — don't silently revisit)

1. **Hyper-V transport:** a **Windows agent installed on the host**, reimplemented natively in .NET
   (no winrun.py / Python in the product). **ENG-0001 (Decided, choice B):** the agent ships in
   2.0/2.2 and drives migration from day one — there is **no winrun.py fallback**, so "agent can
   drive guest control + the migration step graph" is the **gate** for starting 2.3. winrun.py +
   `centralized-access.md` survive as the behavioral reference spec. The agent's mTLS identity
   eliminates storing any Windows domain password.
2. **Persistence: hybrid** — registry/jobs/history persisted (SQLite/EF Core); **secrets never
   plaintext at rest**. **ENG-0002 (Decided):** `ISecretStore` provider abstraction; v1 = app-native
   envelope encryption (AES-GCM in SQLite, DEK wrapped by a runtime-injected KEK); Vault/OpenBao &
   Azure Key Vault as optional providers later; **bind "prefer scoped keys/tokens over passwords."**
3. **Migration: orchestrate proven tools.** The validated path (from the `migrate-vm` skill) is
   **`qm importdisk` on the Proxmox node** + targeted guest fixes; **virt-v2v is optional**, not the
   baseline. ⚠️ ARCHITECTURE.md and `specs/migration-job-model.md` still over-index on virt-v2v —
   **a docs reconciliation is pending** (held for owner review).
4. **Single-operator** (not multi-tenant) — no `tenant_id` in the schema.
5. **Dashboard:** ship direction **A (unified list)** as default, with **B (grouped)** as a toggle.
6. **Migration quiesce:** **operator choice per job, with explicit caveats** (static frontend →
   favor uptime/checkpoint; database server → favor consistency/graceful shutdown). Not hardcoded.
7. **Proxmox-first North Star (ENG-0007):** the product moves **entirely off Hyper-V onto Proxmox**;
   Proxmox is the primary platform, Hyper-V's role declines and is primarily a migration source.
   Refines (not supersedes) ENG-0006: the four pillars are **weighted/sequenced**, not equal.
8. **Incremental release cadence (ENG-0007):** ship **each milestone as its own usable release** —
   **v2.0 = planted Observe** (persistent, multi-platform), **then Proxmox management/Deploy**, **then
   HV→PVE migration**. No big-bang release.
9. **Hyper-V = light management (ENG-0007):** during the transition the HV provider supports **basic
   start/stop/reconfigure** — **not** full Proxmox parity, and **not** source-only. **Implication
   (locked):** the **`IVirtualizationProvider` capability model must allow management verbs on the
   Hyper-V provider** (not just read + migrate-source).
10. **Release-1 scope cut (ENG-0007):** **PVE→HV reverse migration** (nice-to-have) and
    **Backup/Restore** (needed eventually) are **out of the first v2 release** — deferred, delivered
    later.

## 3. What exists right now

### Built code (2.0 foundation — see §5 for slice detail + verification)
- **Solution `VMentory.sln`** over two projects (slice 1, `d57d89d`): **`VMentory.Core`** (classlib,
  domain `Models.cs`) + **`VMentory.Web`** (the exe, references Core). `namespace HyperInventory` →
  `VMentory.*` across all files; `Host` ambiguity aliased in `GlobalUsings.cs`. `Providers.*` / `Agent`
  projects deferred to later slices.
- **Provider abstraction** (slice 2, `2cb54fd`) in `VMentory.Core`: `IVirtualizationProvider`
  (lean — `Platform`, `Capabilities`, `QuickConnectAsync`, `ScanAsync`), the `[Flags] ProviderCapability`
  enum + `ProviderCapabilities` record, `PlatformKind`, and a `Host.Platform` discriminator.
  `HyperVProvider` (in `VMentory.Web`) advertises `Inventory|LiveStats` and the live HV inventory reads
  now flow through the seam.
- **Persistence** (slice 3) in `VMentory.Core/Persistence`: EF Core + SQLite (`VMentoryDbContext`,
  `HostRegistrationEntity` + `InventorySnapshotEntity`, `IInventoryStore`/`EfInventoryStore`, `Initial`
  migration). Host registry + inventory snapshots persist; the diff is fed from persisted snapshots;
  always-on in real mode, **`--mock` stays ephemeral**; DB path via `VMENTORY_DB` (container → volume).
  **No secrets persisted** (await `ISecretStore`); `/api/quit` is now graceful-shutdown (no purge).

### Planning docs (`docs/phase2/`)
- `ARCHITECTURE.md` — target topology, `IVirtualizationProvider` model, persistence, migration engine, carry-over table.
- `ROADMAP.md` — milestones **2.0** foundation/re-architecture → **2.1** Proxmox read → **2.2** management → **2.3** migration MVP → **2.4** scale.
- `specs/` — five component specs (provider-abstraction, agent-protocol, proxmox-integration, migration-job-model, persistence-and-security) + index. *Authored by the documentation agent; **pending owner review**; the migration spec needs the virt-v2v→qm-importdisk correction.*
- `PROGRESS.md` — this file.

### Engineering decision workspace (`docs/engineering/`)
- `README.md` — the RFC/ADR protocol. `REGISTER.md` — the board (read first).
- `discussions/0001`–`0007` — **ENG-0001..0007, all Decided** (transport, secret store, install model,
  agent runtime, mTLS PKI, four-pillar scope, Proxmox-first strategy).
- `discussions/0008-rbac-scoped-console-auth.md` — **ENG-0008, Open** — RBAC / scoped console roles
  (backup-operator / vm-operator / admin), distinct from the agent's constrained-verb authz (ENG-0004);
  to be decided before 2.2 auth hardening.

### Agents (`.claude/agents/`)
- `ui-design.md` — owns `design/`; produces mockups + specs; never edits `wwwroot/index.html`.
- `documentation.md` — owns `docs/`; authors/maintains specs.
- `engineering.md` — drives architectural decisions in `docs/engineering/`.

### Design output (`design/`) — **pending owner review**
- Mockups (2026-06-15): `dashboard-unified-list`, `dashboard-grouped`, `migration-wizard-stepper`, `migration-run-controlroom`.
- Proposals + from-design requests for the multi-platform dashboard and the migration wizard; `STATUS.md` updated.

### The migration skill (`migrate-vm/`) — validated ground truth
A working Hyper-V→Proxmox **cold-migration** runbook (last proven 2026-06-12, Ubuntu 22.04 Gen2).
`SKILL.md` + `references/runbook.md` + `references/centralized-access.md` + scripts
(`winrun.py`, `hyperv-prep.ps1`, `fix-guest-netplan.sh`). This is effectively the prototype of
VMentory's migration engine — its step graph, safety rules, and scripts feed the design.

## 4. Open decisions / what needs the owner

- **ENG-0001 (Decided 2026-06-15 — B):** agent first, .NET-native, no winrun.py fallback; agent is
  the 2.3 gate. Propagation pending in `agent-protocol.md` / `migration-job-model.md` / ROADMAP.
- **ENG-0002 (Decided 2026-06-15):** `ISecretStore` + app-native envelope encryption, runtime-
  injected KEK, tokens-over-passwords (binding). Propagation pending in `persistence-and-security.md`.
- **ENG-0003 (Decided 2026-06-15):** agent install is **manual/org-managed only** (MSI/GPO/SCCM) +
  enrollment token; no remote push-install; VMentory never gets an admin cred.
- **ENG-0004 (Decided 2026-06-15):** agent = NativeAOT single binary, gRPC/HTTP2+mTLS, **constrained
  verb executor** (not a shell), gMSA-preferred (local fallback), **self-update over its own mTLS
  channel** with watchdog rollback. The agent runtime + enrollment + self-update are the **2.0/2.2
  gate for 2.3 migration**. Propagation pending in `agent-protocol.md` / `migration-job-model.md` /
  ROADMAP.
- **ENG-0005 (Decided 2026-06-16):** mTLS PKI = **private CA inside Core** (external-CA seam for
  later), **root+intermediate**, **short-lived agent certs + auto-renew over the mTLS channel**
  (revocation = stop-renew + registry allow/deny, no CRL/OCSP). Enrollment = single-use token + CSR,
  key never leaves host. CA key in `ISecretStore`. Propagation pending in `agent-protocol.md` /
  `persistence-and-security.md`.
- **ENG-0007 (Decided 2026-06-16):** **Proxmox-first North Star** + **incremental release cadence** +
  **light HV management** (provider must allow HV management verbs) + **PVE→HV and Backup out of
  release 1**. Refines ENG-0006 (weighted/sequenced pillars: Observe-plant → Proxmox mgmt/Deploy →
  HV→PVE). ✅ **Propagated** into **ARCHITECTURE.md** (provider capability model) and **ROADMAP.md**
  (release definitions/sequencing) — commits `8c216ae` / `087b260`. See
  `discussions/0007-product-strategy-release-scope.md`.
- **ENG-0008 (Open, 2026-06-16):** **RBAC / scoped console roles** (backup-operator / vm-operator /
  admin) for the web UI — distinct from the agent's constrained-verb authz (ENG-0004). Shapes the
  `app_user.role` schema and every write verb's authorization. **Needs an owner decision** before 2.2
  auth hardening; specs note the dependency and proceed against a small fixed-role recommendation. See
  `discussions/0008-rbac-scoped-console-auth.md`.
- **Persistence open questions — now active (slice 3 shipped).** Engineering agent to pick up, since
  durable storage now exists: (a) **snapshot retention / cadence** — how often to snapshot and how long
  to keep, to bound SQLite growth (no pruning today; every successful scan writes a snapshot); (b)
  **DB at-rest encryption** — the DB holds no plaintext secrets, but inventory/audit may be sensitive
  (encrypt the SQLite file / require encrypted Postgres, or treat the volume as the trust boundary).
  Both are in `persistence-and-security.md §7`; consider promoting to ENG topics. **`ISecretStore`
  (ENG-0002) is the next build slice** — until it lands, the registry persists **no** credentials.
- **Docs reconciliation (held):** demote virt-v2v, ground `migration-job-model.md` in the skill,
  fold in the safety rules + scripts. Held pending owner review of the agents' first output.
- **Review backlog:** the five specs and the four design mockups are first-drafts awaiting owner review.
- Lower-priority open questions captured by the doc agent: conversion-host placement detail, stable
  VM identity across migration, Secure-Boot guest scope. *(mTLS PKI ownership → promoted to critical
  path above; secret-store v1 target → resolved by ENG-0002.)*

## 5. Immediate next steps (suggested order)

**Build has started at the 2.0 foundation (planted Observe)** under the ENG-0007 incremental
cadence — implementation is proceeding in parallel with the remaining doc propagation below.

1. ✅ **Propagate ENG-0007** into **ROADMAP.md** and **ARCHITECTURE.md** (release
   definitions/sequencing + `IVirtualizationProvider` capability model with HV management verbs) —
   **done**, committed `087b260` / `8c216ae`.
2. **Begin 2.0 foundation:** plant Observe — rename `HyperInventory` → `VMentory.*`, split projects,
   add persistence, define `IVirtualizationProvider` + capability model (with HV management verbs).
   - ✅ **Slice 1 (project split + rename) done & verified:** `VMentory.sln` + `VMentory.Core`
     (domain) + `VMentory.Web` (exe) stood up, namespace `HyperInventory` → `VMentory.*` across all
     files, build scripts retargeted, zero behavior change. `Providers.*` / `Agent` projects deferred
     to their slices. **Verified:** `dotnet build` clean (0/0); mock-mode run serves the SPA + returns
     the 5 mock hosts with correct VM counts/totals + enforces the auth token (401); `build.ps1`
     produces `dist\VMentory.exe` (45 MB), and the **published exe itself** was launched (elevated)
     and reproduces the full dashboard (HTML 200, `/api/state`, JSON export, auth gate). _(Env notes
     for this machine: .NET 8 SDK was installed and `nuget.org` added as a package source to enable
     the self-contained publish.)_
   - ✅ **Slice 2 (provider abstraction + capability model) done & verified:** `VMentory.Core` now
     has `IVirtualizationProvider`, the full `[Flags] ProviderCapability` enum + `ProviderCapabilities`
     (HV mgmt verbs allowed per ENG-0007), and `PlatformKind`; `Host` gained a `Platform` discriminator
     (defaults HyperV). `HyperVProvider` (in `VMentory.Web`, wrapping `Scanner`/`Reachability`)
     advertises `Inventory|LiveStats`, and the live HV inventory reads (quick-connect + `/api/scan`)
     now flow through the seam. **Verified:** build 0/0; mock run unchanged with `platform:"HyperV"` on
     every host + 401 auth gate; `dist\VMentory.exe` publishes and serves. _Provider lives in Web for
     now (owner choice); `Providers.HyperV` deferred to the agent slice._
   - ✅ **Slice 3 (persistence) done & verified:** EF Core + SQLite in `VMentory.Core/Persistence`
     (`Initial` migration). Host registry + inventory snapshots persist; diff fed from persisted
     snapshots; `--mock` ephemeral; `VMENTORY_DB` env override; no secrets persisted; `/api/quit`
     graceful (no purge). **Verified:** build 0/0; add-host→restart→persists→delete→gone; mock writes
     no DB; single-file `dist\VMentory.exe` loads the SQLite native lib + persists. _(Scan-driven
     snapshot save is wired + code-traced; full exercise needs a real WinRM host.)_
   - Next: **`ISecretStore`** (envelope encryption, runtime-injected KEK — ENG-0002) so creds/tokens
     can persist; then the NativeAOT agent + enrollment + internal CA. Verb methods
     (lifecycle/migration) + `ProxmoxProvider` land in 2.2 / 2.1.
   - **Auth:** scoped/role-based console access (backup-operator vs vm-operator vs admin) raised by the
     owner → **ENG-0008 (Open)** in the register; to be designed before 2.2 auth hardening.
3. Owner reviews the five specs (`docs/phase2/specs/`) and the four design mockups (`design/`).
4. Reconcile the migration docs (virt-v2v → qm-importdisk, fold in the skill).

## 6. How to resume on a new machine
```
git clone <repo> && cd VMentory && git checkout dev
```
Read in this order: this file → `ARCHITECTURE.md` → `ROADMAP.md` → `docs/engineering/REGISTER.md`
→ the `specs/` you need. The three agents are defined in `.claude/agents/` and can be invoked by
name. CLAUDE.md still describes Phase 1 accurately (plus a Phase 2 pointer at the top).

> Note: agent **memories** live outside the repo (in the local Claude profile) and do **not** travel
> with the clone — this doc is the portable source of truth.
