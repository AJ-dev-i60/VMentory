# VMentory Phase 2 — Progress & Resumption

> **Purpose:** pick up Phase 2 from a clean clone on any machine. Read this top-to-bottom and you
> know where we are, what's decided, what's open, and what to do next.
>
> **Last updated:** 2026-06-17 (re-baselined slice (1) — containerized Core + hosted bootstrap — built & verified on `dev`) · **Phase:** 2.0 foundation — original build slices 1–3 + re-baselined slice (1) landed; next is re-baselined slice (2) login + RBAC · **Branch:** `dev`
>
> ✅ **Foundation RE-BASELINED and APPROVED (2026-06-16).** The development freeze is **lifted**. The
> corrected foundation is locked in the decision records and propagated into ARCHITECTURE/ROADMAP:
> **containerized Core (web-first, install-nothing — ENG-0010), login + RBAC early (ENG-0008),
> Proxmox via native REST API + constrained SSH and NO node agent (ENG-0009), and the on-device
> agent/private-CA foundation demoted + scoped to the Hyper-V migration source (ENG-0001/0003/0004/0005
> amended).** Re-baselined foundation slice (1) — **Containerized Core + hosted-service bootstrap**
> (ENG-0010) — is now **built & verified on `dev`** (2026-06-17): hosted `0.0.0.0` bind, Core-terminated
> HTTPS, Linux container image authored. Build resumes at re-baselined slice (2): **Login + RBAC**
> (ENG-0008) — the session token is removed there (§5).

---

## 1. Orientation (60 seconds)

VMentory Phase 1 = single-exe, **Windows-only, read-only, ephemeral** Hyper-V inventory tool
(ASP.NET Core 8 + vanilla-JS SPA, namespace `HyperInventory`, in-memory only, WinRM via
`powershell.exe`). It works and ships today.

Phase 2 = turn it into a **container-based, single-operator, multi-platform (Hyper-V + Proxmox)
platform** — a Linux-container "Core" (web-first, install-nothing, login + RBAC — ENG-0010/0008) that
talks to providers, persists state, and runs operations. **Proxmox is driven by its native REST API +
a constrained SSH key, no node agent (ENG-0009);** an on-device agent exists **only** on the retiring
**Hyper-V** hosts, as the migration source (ENG-0001, demoted off the 2.0 critical path). The only
installs are (a) the Core container and (b) the Hyper-V agent on the HV hosts.

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
[ROADMAP.md](ROADMAP.md). **Phase 2 implementation has started** at the 2.0 foundation — the original
build slices 1 (project split / rename), 2 (provider abstraction + capability model) and 3
(persistence) are landed and verified on `dev` (see §3 and §5). After the **2026-06-16 re-baseline**,
**re-baselined foundation slice (1): containerized Core + hosted-service bootstrap (ENG-0010) is also
built & verified** (2026-06-17). The next build is **re-baselined slice (2): login + RBAC (ENG-0008)**,
then `ISecretStore` (ENG-0002), then the Proxmox API provider — see §5. **The agent is no longer next**
(demoted + HV-scoped, ENG-0009).

## 2. Locked decisions (the spine — don't silently revisit)

1. **Hyper-V transport:** a **Windows agent installed on the host**, reimplemented natively in .NET
   (no winrun.py / Python in the product). **ENG-0001 (Decided, choice B; amended 2026-06-16):** the
   agent is **scoped to the Hyper-V migration source only** and **demoted off the 2.0 critical path** —
   it now lands **with the migration slice** (re-baselined foundation slice 6, 2.3-era), **not** as the
   first foundation work. There is still **no winrun.py fallback**, so "agent can drive guest control +
   the migration step graph" remains the **gate for 2.3** — built then, not first. winrun.py +
   `centralized-access.md` survive as the behavioral reference spec. The agent's mTLS identity
   eliminates storing any Windows domain password. **Proxmox uses no agent (ENG-0009, item 11).**
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
11. **Proxmox deep-action transport (ENG-0009, Decided 2026-06-16 — choice A):** **native PVE REST API
    as the primary control plane** (orchestration, lifecycle, provisioning, stats/`rrddata`, native
    PVE↔PVE migration, UPID task progress) + a **constrained, forced-command SSH key** for the disk-
    import / conversion / on-node guest-edit residue (`qm importdisk`, `qemu-img`, optional `virt-v2v`,
    mount-and-edit fixes). **NO agent on Proxmox nodes** — onboard = scoped API token + SSH key, both in
    `ISecretStore`, both revocable. This is **why** the agent foundation (item 1) is demoted + HV-scoped.
12. **Console RBAC (ENG-0008, Decided 2026-06-16 — firm + early):** **fixed built-in roles** (Admin /
    VM-operator / Backup-operator / Viewer) over an internal **pillar×verb catalog** reusing
    `ProviderCapability` + a small console-permission set; **local accounts now**, **OIDC seam later**;
    a **single audited authz chokepoint** (Web/API + ops engine) that **must exist before any write
    verb**. Login lands in the deployment slice; the role framework before the first write verbs.
13. **Core deployment & runtime (ENG-0010, Decided 2026-06-16):** **containerized Core**, bind
    **`0.0.0.0:{configurable port}`**, **Core-terminated HTTPS** via Kestrel (self-signed/provided cert;
    reverse-proxy a documented future seam, **not** required for release 1), **runtime KEK + TLS
    injection** (nothing baked into the image), SQLite on a **mounted volume**, and the
    **session-token/loopback bootstrap replaced by login + RBAC**. The user **installs nothing locally**
    beyond the container; the only two installs are the **Core container** and the **Hyper-V agent**.

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
- **Containerized Core + hosted-service bootstrap** (re-baselined slice (1), ENG-0010) — `Program.cs`
  bootstrap rewritten from the loopback desktop model to a hosted service. Binds **`VMENTORY_HTTP_ADDR`
  (default `0.0.0.0`)** : **`VMENTORY_HTTP_PORT`** via `ConfigureKestrel`; `FindFreePort()` + the
  `127.0.0.1:{random}` bind are removed. **Core-terminated HTTPS** via `listen.UseHttps(cert)`;
  **`VMENTORY_HTTP_ONLY=1`** disables TLS (reverse-proxy/dev). New **`TlsSetup.cs`** resolves the cert
  with precedence **operator PFX → operator PEM → self-signed fallback** (self-signed cached in the data
  dir in real mode for a stable identity / one-time warning; mock mode ephemeral, writes nothing —
  nothing baked into the image). Desktop affordances removed (`OpenBrowser()`, the R-key reopen); the
  **console Q-to-quit loop now runs only when `!Console.IsInputRedirected`** (container has no TTY →
  skipped; operator stops via SIGTERM → ASP.NET graceful shutdown). WinRM-service ensure is now
  **`OperatingSystem.IsWindows()`-guarded** (Linux container has no PowerShell host). New **`Dockerfile`
  + `.dockerignore`**: single multi-stage Linux image (sdk build → aspnet runtime), installs
  **openssh-client** (ENG-0009 SSH executor), **no `qemu`/`qm`**, runs **non-root (uid 10001)**, DB +
  cached cert on the **`/data` volume**, `EXPOSE 8443`. `AppConfig` gained `HttpAddr`/`HttpOnly`/`DataDir`;
  the csproj `ApplicationManifest` is now Windows-only-conditioned so the Linux publish doesn't choke on
  the requireAdministrator manifest. **Interim auth: the single session token is KEPT** (printed at
  startup / container logs) — login + RBAC that retires it is **slice (2)**, not landed here. `/api/quit`
  was **left in place** (graceful, already neutered) — a desktop affordance still pending retirement,
  coupled to a UI quit button that must go through the design workflow. **Caveat:** the Dockerfile is
  **authored but unbuilt** (no Docker on this Windows host) — the image has not been built/run; only the
  app bootstrap was verified (see §5).

### Planning docs (`docs/phase2/`)
- `ARCHITECTURE.md` — target topology, `IVirtualizationProvider` model, persistence, migration engine, carry-over table.
- `ROADMAP.md` — milestones **2.0** foundation/re-architecture → **2.1** Proxmox read → **2.2** management → **2.3** migration MVP → **2.4** scale.
- `specs/` — five component specs (provider-abstraction, agent-protocol, proxmox-integration, migration-job-model, persistence-and-security) + index. *Authored by the documentation agent; **pending owner review**; the migration spec needs the virt-v2v→qm-importdisk correction.*
- `PROGRESS.md` — this file.

### Engineering decision workspace (`docs/engineering/`)
- `README.md` — the RFC/ADR protocol. `REGISTER.md` — the board (read first).
- `discussions/0001`–`0010` — **ENG-0001..0010, all Decided.** Transport, secret store, install model,
  agent runtime, mTLS PKI, four-pillar scope, Proxmox-first strategy, **RBAC (0008)**, **Proxmox
  REST+SSH / no node agent (0009)**, **containerized Core deployment model (0010)**. The **2026-06-16
  re-baseline** (0009/0010, amendments to 0001/0003/0004/0005) demotes the agent/CA foundation to
  Hyper-V and re-orders the foundation slices — see §2 and §5.

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

- **ENG-0001 (Decided 2026-06-15 — B; amended 2026-06-16):** agent .NET-native, no winrun.py
  fallback; agent is the 2.3 gate — but now **scoped to the Hyper-V source and demoted off the 2.0
  critical path** to the migration slice (via ENG-0009). Propagation pending in `agent-protocol.md` /
  `migration-job-model.md`; **ROADMAP/ARCHITECTURE/PROGRESS reflect the amendment.**
- **ENG-0002 (Decided 2026-06-15):** `ISecretStore` + app-native envelope encryption, runtime-
  injected KEK, tokens-over-passwords (binding). Propagation pending in `persistence-and-security.md`.
- **ENG-0003 (Decided 2026-06-15):** agent install is **manual/org-managed only** (MSI/GPO/SCCM) +
  enrollment token; no remote push-install; VMentory never gets an admin cred.
- **ENG-0004 (Decided 2026-06-15):** agent = NativeAOT single binary, gRPC/HTTP2+mTLS, **constrained
  verb executor** (not a shell), gMSA-preferred (local fallback), **self-update over its own mTLS
  channel** with watchdog rollback. **Amended 2026-06-16:** Windows-HV only, **descoped from 2.0
  front-loading** — the agent runtime + enrollment + self-update are the gate for **2.3 migration** but
  are built **with** that slice (foundation slice 6), not first (via ENG-0009). Propagation pending in
  `agent-protocol.md` / `migration-job-model.md`.
- **ENG-0005 (Decided 2026-06-16; amended 2026-06-16):** mTLS PKI = **private CA inside Core**
  (external-CA seam for later), **root+intermediate**, **short-lived agent certs + auto-renew**
  (revocation = stop-renew + registry allow/deny, no CRL/OCSP). Enrollment = single-use token + CSR,
  key never leaves host. CA key in `ISecretStore`. **Amended:** the CA serves the **HV fleet only** and
  is **deferred from 2.0 to the migration slice**; it is a **distinct trust domain from the ENG-0010
  dashboard TLS**. Propagation pending in `agent-protocol.md` / `persistence-and-security.md`.
- **ENG-0007 (Decided 2026-06-16):** **Proxmox-first North Star** + **incremental release cadence** +
  **light HV management** (provider must allow HV management verbs) + **PVE→HV and Backup out of
  release 1**. Refines ENG-0006 (weighted/sequenced pillars: Observe-plant → Proxmox mgmt/Deploy →
  HV→PVE). ✅ **Propagated** into **ARCHITECTURE.md** (provider capability model) and **ROADMAP.md**
  (release definitions/sequencing) — commits `8c216ae` / `087b260`. See
  `discussions/0007-product-strategy-release-scope.md`.
- **ENG-0008 (Decided 2026-06-16):** **fixed console roles** (Admin / VM-operator / Backup-operator /
  Viewer) over a **pillar×verb catalog** reusing `ProviderCapability` + a small console-permission set;
  **local accounts now**, **OIDC seam later**; a **single audited authz chokepoint** (Web/API + ops
  engine) that **must exist before any write verb**; login in the deployment slice, roles before the
  first write verbs. Distinct from the agent's constrained-verb authz (ENG-0004). Shapes the
  `app_user`/role schema. Propagation pending in `persistence-and-security.md`;
  **ARCHITECTURE §Security/ROADMAP reflect it.** See `discussions/0008-rbac-scoped-console-auth.md`.
- **ENG-0009 (Decided 2026-06-16 — A):** **Proxmox via native REST API (primary) + constrained SSH
  key; NO node agent.** This is what **demotes/scopes the agent foundation (0001/0003/0004/0005) to
  Hyper-V** and off the 2.0 critical path. Propagation pending in `proxmox-integration.md` (SSH
  hardening + known-hosts detail); **ARCHITECTURE/ROADMAP/PROGRESS reflect it.** See
  `discussions/0009-proxmox-deep-action-transport.md`.
- **ENG-0010 (Decided 2026-06-16; BUILT 2026-06-17):** **containerized Core**, `0.0.0.0` bind,
  **Core-terminated HTTPS** (Kestrel; reverse-proxy a documented future seam, not required for release 1),
  **runtime KEK + TLS injection**, SQLite on a mounted volume, **session-token/loopback → login + RBAC**.
  First foundation slice. ✅ **Built & verified on `dev` (re-baselined slice (1), §5):** hosted-service
  bootstrap, `TlsSetup.cs` (PFX→PEM→self-signed), `Dockerfile`/`.dockerignore`, non-root `/data` volume
  image. **Carry-overs:** the session token is **still interim** (login lands in slice (2), not here);
  `/api/quit` retirement + `Updater.cs` image-tag re-scope are pending; the container image is authored
  but **unbuilt**. ARCHITECTURE (Deployment-model / Topology / §5 Security) + ROADMAP now reflect what
  shipped; `persistence-and-security.md §5a` already describes the model (decided, forward-looking). See
  `discussions/0010-core-deployment-runtime-model.md`.
- **Persistence open questions — now active (slice 3 shipped).** Engineering agent to pick up, since
  durable storage now exists: (a) **snapshot retention / cadence** — how often to snapshot and how long
  to keep, to bound SQLite growth (no pruning today; every successful scan writes a snapshot); (b)
  **DB at-rest encryption** — the DB holds no plaintext secrets, but inventory/audit may be sensitive
  (encrypt the SQLite file / require encrypted Postgres, or treat the volume as the trust boundary).
  Both are in `persistence-and-security.md §7`; consider promoting to ENG topics. **`ISecretStore`
  (ENG-0002) is the next build slice** — until it lands, the registry persists **no** credentials.
- **ENG-0011 (Open / Raised 2026-06-17 — discuss + plan, NOT decided):** **observability / structured
  logging / failure-surfacing.** Trigger: the containerized Core on Coolify reports every HV host
  "unreachable" with no *why* (Linux container has no PowerShell/WinRM host + no line-of-sight). Design a
  capture→correlate→surface→persist pipeline + structured logging (`Microsoft.Extensions.Logging`?
  stdout-first vs SQLite-persisted) **before** the failure-prone surfaces (Proxmox REST+SSH, ops/migration
  engine, agent comms, scheduling, data-at-scale) land at scale, so they fail loudly. Ties to ENG-0008
  audit chokepoint, ENG-0009 transports, ENG-0010 stdout/env config, ENG-0002 secrets-never-logged, and
  the open §7 retention/encryption questions above. See `discussions/0011-observability-logging.md`.
- **Docs reconciliation (held):** demote virt-v2v, ground `migration-job-model.md` in the skill,
  fold in the safety rules + scripts. Held pending owner review of the agents' first output.
- **Review backlog:** the five specs and the four design mockups are first-drafts awaiting owner review.
- Lower-priority open questions captured by the doc agent: conversion-host placement detail, stable
  VM identity across migration, Secure-Boot guest scope. *(mTLS PKI ownership → promoted to critical
  path above; secret-store v1 target → resolved by ENG-0002.)*

## 5. Immediate next steps (suggested order)

**The foundation is re-baselined and approved (2026-06-16); the freeze is lifted.** Re-baselined slice
(1) — **containerized Core + hosted-service bootstrap** — is now **built & verified on `dev`**
(2026-06-17). Build resumes at **re-baselined foundation slice (2): login + RBAC.** The approved
slice order is:

> **(1) Containerized Core + hosted-service bootstrap (ENG-0010) ✅ → (2) Login + RBAC framework
> (ENG-0008) ← NEXT → (3) `ISecretStore` (ENG-0002) → (4) Proxmox API provider (read / planted Observe) →
> (5) Proxmox SSH executor + management/Deploy write verbs (capability-gated, RBAC-enforced, audited)
> → (6) Hyper-V agent + private CA (scoped to HV, ENG-0001/0003/0004/0005) → (7) HV→PVE migration.**

**Done — slice (1):** the desktop bootstrap in [`Program.cs`](../../Program.cs) was replaced by a hosted
service — `0.0.0.0:{configurable port}` (env), Core-terminated HTTPS via Kestrel (`TlsSetup.cs`, cert
precedence PFX → PEM → self-signed, nothing baked in), TTY-gated console loop, `IsWindows()`-guarded
WinRM ensure, and a single Linux `Dockerfile` (app + SSH client, no `qemu`/`qm`, non-root, `/data`
volume). **Two carry-overs remain:** (a) the **interim session token + `/api/quit` desktop route** are
still present — the session token is retired in **slice (2)**, and `/api/quit` (+ its UI quit button) is
a desktop affordance still pending retirement via the `design/` workflow; (b) **`Updater.cs` re-scope**
(GitHub-exe auto-update → container image tags) is **not yet done**. The **container image is unbuilt**
(no Docker on this host) — build/run it before relying on it.

**Do next — slice (2):** layer **login + the RBAC chokepoint** on top — minimal admin login **removes
the interim session token**, fixed roles (Admin / VM-operator / Backup-operator / Viewer) over a
pillar×verb catalog reusing `ProviderCapability`, a single audited authz chokepoint before any write
verb (ENG-0008). **The agent is still not next** — it is HV-scoped and lands at slice (6) with migration
(ENG-0009).

1. ✅ **Propagate ENG-0007** into **ROADMAP.md** and **ARCHITECTURE.md** (release
   definitions/sequencing + `IVirtualizationProvider` capability model with HV management verbs) —
   **done**, committed `087b260` / `8c216ae`.
2. **2.0 foundation — original build slices 1–3 landed** (project split, provider abstraction,
   persistence); the **re-baselined slice order above** governs what comes next.
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

   **Re-baselined foundation slices (post-2026-06-16):**
   - ✅ **Re-baselined slice (1) (containerized Core + hosted-service bootstrap, ENG-0010) done &
     verified (2026-06-17):** `Program.cs` rewritten loopback-desktop → hosted service — binds
     `VMENTORY_HTTP_ADDR` (default `0.0.0.0`) : `VMENTORY_HTTP_PORT` via `ConfigureKestrel`,
     `FindFreePort()`/loopback removed; **Core-terminated HTTPS** (`listen.UseHttps`), `VMENTORY_HTTP_ONLY=1`
     disables TLS. New `TlsSetup.cs` (cert precedence operator-PFX → operator-PEM → self-signed,
     real-mode cached / mock ephemeral, nothing baked in). Desktop affordances dropped (`OpenBrowser`,
     R-key); console Q-loop gated on `!Console.IsInputRedirected`; WinRM ensure `IsWindows()`-guarded.
     New `Dockerfile` + `.dockerignore` (multi-stage, openssh-client, no `qemu`/`qm`, non-root uid 10001,
     `/data` volume, `EXPOSE 8443`); csproj manifest Windows-only-conditioned; `AppConfig` +
     `HttpAddr`/`HttpOnly`/`DataDir`. **Interim auth: session token KEPT** (login/RBAC is slice (2));
     `/api/quit` left in place (graceful) — retirement still pending. **Verified:** `dotnet build
     VMentory.sln` clean (0/0); DLL run in mock mode bound `0.0.0.0:8444` over **self-signed HTTPS**, SPA
     200 over HTTPS, `/api/state` 401 without token / 200 with token returning all 5 mock hosts;
     `VMENTORY_HTTP_ONLY=1` served plain HTTP 200 + the 401 auth gate. **Caveat — container image
     UNBUILT:** no Docker on this Windows host, so the Dockerfile is authored but **not built/run**; only
     the app bootstrap was exercised. _The `/api/quit` route + its UI quit button remain a desktop
     affordance pending retirement (the UI change must go through the `design/` workflow)._
   - **Next (re-baselined):** slice (2) **login + RBAC** (ENG-0008) — minimal admin login **removes the
     interim session token** + lands the fixed-role framework / pillar×verb catalog / single audited
     authz chokepoint, before any write verb. Then (3) **`ISecretStore`** (ENG-0002) → (4) **Proxmox API
     read provider**. Proxmox write verbs (5) and the **HV-scoped agent + private CA** (6) follow;
     HV→PVE migration (7). **The agent is still not next** (demoted + HV-scoped, ENG-0009).
   - **Auth:** scoped/role-based console access (Admin / VM-operator / Backup-operator / Viewer) →
     **ENG-0008 (Decided 2026-06-16)** — fixed roles over a pillar×verb catalog, local accounts now,
     single audited authz chokepoint **before any write verb**; login lands in the slice-(1) deployment
     work, the role framework before slice-(5) write verbs.
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
