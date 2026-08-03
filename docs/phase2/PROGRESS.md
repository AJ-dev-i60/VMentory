# VMentory Phase 2 — Progress & Resumption

> **Purpose:** pick up Phase 2 from a clean clone on any machine. Read this top-to-bottom and you
> know where we are, what's decided, what's open, and what to do next.
>
> **Last updated:** 2026-08-03 (**monitoring-first re-focus** — see the handoff note below)
> · **Phase:** 2.0 foundation — original build slices 1–3 + re-baselined slices (1)–(4) all built &
> verified; deployed on Coolify; **next is the re-sequenced slice (5): named credentials (ENG-0012)**,
> ahead of the health model and the Hyper-V SSH transport · **Branch:** `dev`
>
> ---
>
> ### 🔻 Handoff note — 2026-08-03 session · PRIMARY FOCUS CHANGED
>
> **VMentory's primary focus is now a monitoring tool for Hyper-V *and* Proxmox hosts.** Observe stops
> being the *plant beneath the pillars* and becomes **the product**. Recorded as an **amendment to
> ENG-0007** (not a supersession) plus a new **ENG-0013**. Nothing was built this session — this is a
> re-baseline of direction, plus a repo hygiene fix.
>
> **The blocker this exposed — ENG-0013.** Hyper-V is **wholly unreachable from the containerized
> Core**, and always has been since slice (1) containerized it: `Reachability.cs:169` shells
> `powershell.exe`, `:20` shells `ping.exe`, `:52,80-82` ensure the *local* WinRM service and mutate
> `TrustedHosts`, and `Scanner.cs:216` runs `Invoke-Command -ComputerName` — i.e. the Core is assumed
> to *be* the domain WinRM client. On Linux only `Program.cs:517`'s bare TCP probe survives, so a
> healthy HV host presents as "port open, every scan failed." **This is the root cause of the ENG-0011
> trigger** ("every HV host unreachable with no *why*"), which until now had been read as a logging gap.
>
> **Decided (2026-08-03, owner):**
> 1. **ENG-0013 — Hyper-V monitors over SSH + PowerShell executed on the host** (in-box OpenSSH Server
>    capability + key auth; no Windows password at rest). A single **`ISshExecutor`** seam in
>    `VMentory.Core` serves **both** platforms — HV commands *and* the ENG-0009 Proxmox residue — so the
>    executor is built **once, for monitoring**, instead of arriving with the Proxmox write verbs.
> 2. **ENG-0007 amended** — weighting becomes **Observe (both platforms, to depth) → Proxmox
>    management/Deploy → HV→PVE migration**. HV reaches **monitoring parity only**; management stays
>    light and HV still declines ("monitor it well *while* it declines").
> 3. **Scope bound — health + inventory, no metrics.** No time-series store, trend charts, thresholds
>    or alerting. Those are unraised and unbudgeted and would need their own ENG topic.
> 4. **Slice (5) (Proxmox SSH executor + write verbs) is demoted**; see the re-sequenced order in §5.
>
> **→ documentation agent:** ARCHITECTURE.md still describes the Hyper-V transport as WinRM-via-Core
> and the agent as HV's eventual transport — both now wrong; reconcile against ENG-0013. The
> `specs/agent-protocol.md` scope shrinks to migration only.
> **→ engineering agent:** ENG-0013's build-time sub-Qs are open — plain `ssh` exec vs pwsh-7 PSRP
> (recommend plain; 5.1 is in-box), and the SSH user model, which should be resolved *together with*
> ENG-0012's still-open root-vs-per-node question. **Nothing is proven against a live HV host yet** —
> that is the slice acceptance gate.
> **→ design agent:** the multi-platform dashboard pick (unified list vs platform-grouped) is now on
> the critical path, not a 2.1 nicety — it is where "monitoring, both platforms" becomes visible.
>
> **Repo hygiene (same session):** the 2026-07-30 home-restore had rewritten all 72 tracked files
> LF→CRLF, showing as a 10,450-line no-op diff. Working tree reset and a **`.gitattributes`**
> (`* text=auto`) added so it cannot recur.
>
> ---
>
> ### 🔻 Handoff note — 2026-06-19 session (for the doc / engineering / design agents to integrate)
>
> First **live Proxmox onboarding** of vega14 (PVE 9.2.2 at `172.0.0.14`) succeeded end-to-end —
> `ProxmoxProvider` now pulls real inventory (72 cores / 251 GB / 2 VMs). Five things shipped to `dev`
> this session (all verified, build clean):
> 1. **`fix(proxmox)` `6d3732e`** — the real blocker: `ProxmoxProvider.BuildClient` now sends the PVE
>    token via **`TryAddWithoutValidation`**. `.NET`'s validating `Headers.Add` threw `FormatException`
>    on PVE's `PVEAPIToken=user@realm!tokenid=uuid` scheme *before the request was sent*, surfacing as a
>    generic "Unexpected error during connect" → every Proxmox host looked unreachable. **(CLAUDE.md
>    gotcha #13.)** Stored token is the **bare** form (no `PVEAPIToken=` prefix — provider adds it).
> 2. **`fix(ui)` `79e5a91`** — host reachability is now driven by the **management plane (port + auth),
>    not ICMP**. PVE blocks ping, so a fully-working host showed red "Unreachable"; ICMP is now
>    informational (neutral grey **P** badge). *(The deployed Coolify instance reaches vega14 fine — it's
>    a VM on that same `172.0.0.0/24` host; the "unreachable" was purely this ICMP bug.)*
> 3. **`fix(ui)` `6a304c7`** — the forced **global WinRM-creds wall on every login is removed** (intrusive
>    for the token-based Proxmox flow; superseded by ENG-0012). Overlay markup left dormant.
> 4. **`feat(hosts)` `6a25701`** — **edit registered hosts**: rename (`Host.DisplayName`, new
>    `AddHostDisplayName` migration) + re-enter credentials, via **`PATCH /api/hosts/{id}`** and a pencil
>    icon (overview row) + Edit button (host page). Interim until ENG-0012.
> 5. Token-entry **hint + client-side validation** in add-host/edit modals (catches the bare-UUID paste).
>
> **→ documentation agent:** fold items 1–5 into §3/§5 + the slice-(4) record; the API surface now has
> `PATCH /api/hosts/{id}`; `Host` has `DisplayName`.
> **→ engineering agent:** **ENG-0012 (credential-management revamp) is newly Open** in `REGISTER.md` —
> resolve the named/reusable-credential model **before slice (5)** adds the SSH key as a 2nd per-host
> secret. Live Proxmox now proven, de-risking slice (5). Consider whether the ICMP→informational change
> + "honest failure" surfacing belongs under ENG-0011.
> **→ design agent:** new from-codebase request `2026-06-19-credential-management.md` (credential surface
> + structured PVE-token entry) is on the board; the interim edit-host modal + token hint are live and
> want a designed version.
>
> ---
>
> ✅ **Foundation RE-BASELINED and APPROVED (2026-06-16).** The development freeze is **lifted**. The
> corrected foundation is locked in the decision records and propagated into ARCHITECTURE/ROADMAP:
> **containerized Core (web-first, install-nothing — ENG-0010), login + RBAC early (ENG-0008),
> Proxmox via native REST API + constrained SSH and NO node agent (ENG-0009), and the on-device
> agent/private-CA foundation demoted + scoped to the Hyper-V migration source (ENG-0001/0003/0004/0005
> amended).** Re-baselined foundation slices (1)–(4) are all built & deployed (2026-06-17/18):
> hosted `0.0.0.0` bind, Core-terminated HTTPS, Linux container, login + RBAC (session token retired),
> `ISecretStore` AES-256-GCM, **Proxmox API read provider** (`ProxmoxProvider` + `ProviderRegistry`).
> **Next: re-baselined slice (5): Proxmox SSH executor + management/Deploy write verbs.**

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
  Secrets were **not persisted at this point** (ISecretStore landed in re-baselined slice (3)); `/api/quit` is graceful-shutdown (no purge).
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
  the requireAdministrator manifest. **Interim auth: the single session token was KEPT** at this slice
  — login + RBAC that retired it was **slice (2)**. `/api/quit` was **left in place** (graceful, already
  neutered) — a desktop affordance still pending retirement, coupled to a UI quit button that must go
  through the design workflow.
  - **Two additions made during slice (1) (commit `7bd47f2`):** a **`GET /health`** unauthenticated
    liveness probe, and a **`VMENTORY_TOKEN`** env override that pinned a stable interim token (retired
    in slice (2) when cookie-based login replaced it).
  - **DEPLOYED (2026-06-18) — dev instance on Coolify.** The image is now **built and running** on the
    EdgeStudios **Coolify** host at **https://vmentorydev.edgestudios.co.za** — verified serving:
    HTTPS via Coolify/Traefik, `/health` 200, SPA 200, auth gate 401 (no token) / 200 (with token).
    **Reverse-proxy mode:** Traefik terminates TLS; the app runs **`VMENTORY_HTTP_ONLY=1`** and serves
    plain HTTP on **8443**; SQLite lives on a **`/data` named volume**; initially accessed via
    **`VMENTORY_TOKEN`** env (retired in slice (2)). This supersedes the earlier "authored but unbuilt" caveat for the dev
    target. _(The "no Docker on this Windows host" note still holds locally — the image builds/runs on
    the Coolify host, not this workstation.)_
  - **Post-deploy bug fix (commit `ca196f4`):** the non-root container (uid 10001) crash-looped (exit
    139) behind Coolify because `ErrorLogger` wrote `errors.log` into `/app` (root-owned) →
    `UnauthorizedAccessException` at startup. Fixed by resolving the log to a writable path
    (**`VMENTORY_LOG`** → the `VMENTORY_DB` `/data` dir → app base) and making `ErrorLogger` **fail-safe**
    (degrades to a no-op / temp-dir fallback — logging never crashes startup). **Generalizable lesson:
    non-root containers must write only to mounted volumes.**
  - **Deploy tooling lives OUTSIDE this repo.** The Coolify deploy is driven by a separate **private
    `claude-ops` repo** (its `deploy` skill; `ssh edgestudios` → the Coolify host; the API token + app
    UUIDs are server-side only in `/etc/coolify-deploy/config.json`). That repo and the agent memories
    do **not** travel with this clone — this pointer is here so a future session knows where the deploy
    lives. **No secret/token is copied into this repo.**
  - **Auto-deploy webhook (added 2026-06-18):** a GitHub webhook on `AJ-dev-i60/VMentory` fires on
    every push and triggers a Coolify rebuild, signed via HMAC-SHA256 (`X-Hub-Signature-256`). Coolify
    filters on `git_branch = dev`. `VMENTORY_ADMIN_PASSWORD` is configured in the Coolify environment
    (value not in this repo) so the first-admin seed gets a stable password on volume resets.
  - **CI workflow (`.github/workflows/ci.yml`, added 2026-06-18):** triggers on push/PR to `dev` and
    `main`; runs `dotnet restore` + `dotnet build VMentory.sln -c Release`. Separate from
    `release.yml` (fires on `v*` tags, produces the Windows exe release asset).

### Live operational facts (from the deployed dev instance — resume here)
- **The containerized Core cannot inventory Hyper-V — confirmed live.** On the Coolify dev instance,
  **every** HV host reports "unreachable": a Linux container has **no PowerShell/WinRM host** and **no
  LAN line-of-sight** to the HV hosts. This is **by design** and validates the HV-agent / Proxmox-REST
  split (ENG-0001 / ENG-0009). It was the concrete trigger for **ENG-0011** (observability — see §4).
  **To exercise HV inventory today, run Core on Windows** (the dev DLL or the legacy single-file exe)
  with WinRM reach to the HV hosts — not from the container.
- **Live Proxmox test target:** **vega14** (PVE 9.2, same LAN as the container) is the natural first
  live target for slice (4). The container→Proxmox network path works (outbound REST 8006 + SSH 22,
  ENG-0009). Register vega14 as a `Proxmox` host with a scoped API token; the `ProxmoxProvider` will
  call `/api2/json/nodes`, enumerate QEMUs + LXCs, and surface them in the dashboard alongside any HV
  hosts. **Note: one registered Host = one PVE node** — multi-node cluster support (one Host per
  cluster) is deferred to slice (5)+.

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
  image. **DEPLOYED 2026-06-18** as a Coolify dev instance (https://vmentorydev.edgestudios.co.za) — image
  built + running, verified serving (see §3 / §5). **Carry-overs resolved:** session token **retired in
  slice (2)**; `/api/quit` retirement + `Updater.cs` image-tag re-scope remain pending.
  ARCHITECTURE (Deployment-model / Topology / §5 Security) + ROADMAP now reflect what
  shipped; `persistence-and-security.md §5a` already describes the model. See
  `discussions/0010-core-deployment-runtime-model.md`.
- **Persistence open questions — now active (slice 3 shipped).** Engineering agent to pick up, since
  durable storage now exists: (a) **snapshot retention / cadence** — how often to snapshot and how long
  to keep, to bound SQLite growth (no pruning today; every successful scan writes a snapshot); (b)
  **DB at-rest encryption** — the DB holds no plaintext secrets, but inventory/audit may be sensitive
  (encrypt the SQLite file / require encrypted Postgres, or treat the volume as the trust boundary).
  Both are in `persistence-and-security.md §7`; consider promoting to ENG topics. ✅ **`ISecretStore`
  (ENG-0002) is now built (slice (3), 2026-06-18)** — credentials persist when `VMENTORY_KEK` is set;
  see §5.
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

**Foundation slices (1)–(4) are all built and verified; (1)–(3) are deployed on Coolify (2026-06-17/18); slice (4) landed on `dev` (2026-06-18, commit `2907fd4`).** The approved slice order is:

> **(1) Containerized Core + hosted-service bootstrap (ENG-0010) ✅ → (2) Login + RBAC framework
> (ENG-0008) ✅ → (3) `ISecretStore` (ENG-0002) ✅ → (4) Proxmox API provider (read / planted
> Observe) ✅ → (5) Named credentials (ENG-0012) ← NEXT → (6) Health model (ENG-0011a) →
> (7) Hyper-V over SSH + shared `ISshExecutor` (ENG-0013) → (8) Multi-platform monitoring dashboard
> → (9) Proxmox management/Deploy write verbs → (10) Hyper-V agent + private CA
> (ENG-0001/0003/0004/0005) → HV→PVE migration.**

**⚠ Re-sequenced 2026-08-03 (monitoring-first — ENG-0007 amendment + ENG-0013).** The old slice (5)
"Proxmox SSH executor + management/Deploy write verbs" was split: its **SSH-executor half is pulled
forward** into (7) and generalised to serve *both* platforms, and its **write verbs are demoted** to
(9). Slices (5)–(8) are now what make VMentory a monitoring tool for Hyper-V *and* Proxmox.

**Do next — slice (5): named credentials (ENG-0012).** Already **Decided** (2026-06-19) and unbuilt:
flat `CredentialEntity` (TPH + JSON `Descriptor`) + typed `Host` slots
(`ManagementCredentialId`/`TransportCredentialId`), global creds retired via a startup promotion task
after KEK/`DekProvider`, Admin-only vault, CRUD returning metadata only. It was already sequenced ahead
of the old slice (5) because that added an SSH key as a second per-host secret — **ENG-0013 doubles the
argument**, since Hyper-V now grows one too. Both platforms are about to hold two secrets each; the
untyped model must not be what carries them.

**Then (6): the health model (ENG-0011a).** Decided 2026-06-19, unbuilt. Typed
`HealthTier`/`FailureStage`/`HostFault`/`HostHealth` in `VMentory.Core`; `IVirtualizationProvider`
returns a per-stage fault **list**; one evaluator unifies the add-host and poller paths; `health` JSON
over `/api/state`+SSE replaces the `Reachability` booleans and `AddError`/`ScanError`/`ErrorDetail`.
This is the backbone of monitoring on both platforms and it unblocks the dashboard.

**Then (7): Hyper-V over SSH (ENG-0013).** The transport that makes "both platforms" true at all —
Hyper-V is currently **wholly unreachable from the container** (see the handoff note). One
`ISshExecutor` seam in Core, consumed by both providers. `BuildRemoteWrapper`/`Invoke-Command`
(`Scanner.cs:216`), `RunPowerShellAsync` (`Reachability.cs:158-200`), the local WinRM ensure (`:52`)
and the `TrustedHosts` mutation (`:80-82`) are **deleted, not ported**. ICMP (`ping.exe`, `:20`) is
dropped as genuinely optional — the non-root container has no raw sockets, and ENG-0011a already made
it informational-only. **Acceptance gate: an end-to-end read from a real Hyper-V host**, the way vega14
proved the Proxmox path — nothing here is verified against live hardware yet.

**Then (8): the multi-platform monitoring dashboard.** The design pick that has been sitting in
`design/STATUS.md` since 2026-06-15 (unified list **A** vs platform-grouped **B**) is now on the
critical path — it is where "monitoring, both platforms" becomes visible — rendered against
ENG-0011a's six-tier language and fault drill-down.

**Done — slice (1):** the desktop bootstrap in [`Program.cs`](../../Program.cs) was replaced by a hosted
service — `0.0.0.0:{configurable port}` (env), Core-terminated HTTPS via Kestrel (`TlsSetup.cs`, cert
precedence PFX → PEM → self-signed, nothing baked in), TTY-gated console loop, `IsWindows()`-guarded
WinRM ensure, and a single Linux `Dockerfile` (app + SSH client, no `qemu`/`qm`, non-root, `/data`
volume). Image built and deployed as a Coolify dev instance (2026-06-18, https://vmentorydev.edgestudios.co.za).
**Carry-overs:** the **session token** was kept as interim auth (retired in slice (2)); **`/api/quit`**
(+ its UI quit button) remains — desktop affordance still pending retirement via the `design/` workflow;
**`Updater.cs` re-scope** (GitHub-exe auto-update → container image tags) not yet done.

**~~Do next — slice (5): Proxmox SSH executor + management/Deploy write verbs.~~ Superseded 2026-08-03 — this slice was split; see the re-sequenced order above.** Slice (4) is done (commit `2907fd4`, 2026-06-18). The original plan was Proxmox management: the constrained SSH executor for on-node residue (ENG-0009) plus the first write verbs (`start`, `stop`, `shutdown`, `snapshot`) via `ProxmoxProvider`, capability-gated and RBAC-enforced (the first actions requiring the ENG-0008 authz chokepoint to gate a real write). Under the monitoring-first re-focus the **executor half moved forward to slice (7)** and was generalised to serve Hyper-V as well (ENG-0013); the **write verbs are now slice (9)**. The claim in the original text that "HV light management rides the agent" also no longer holds for *monitoring* — ENG-0013 removed the agent from that path entirely.

**Slice (2) carry-overs:**
- **Login UI (`wwwroot/index.html`):** a functional but unstyled interim login + change-password screen
  is live. Design request raised at `design/requests/from-codebase/2026-06-18-login-screen.md`;
  `design/STATUS.md` updated. The designed version lands once the design agent delivers.
- **`/api/quit` + its UI quit button** remain (desktop affordance, pending design workflow retirement).
- **`Updater.cs` re-scope** (exe auto-update → container image tags) not yet done.

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
     `VMENTORY_HTTP_ONLY=1` served plain HTTP 200 + the 401 auth gate. Slice (1) also added a
     **`GET /health`** liveness probe + a **`VMENTORY_TOKEN`** stable-interim-token override (`7bd47f2`).
     `VMENTORY_TOKEN` was **retired in slice (2)** (replaced by cookie-based login + RBAC).
     _The `/api/quit` route + its UI quit button remain a desktop affordance pending retirement (the UI
     change must go through the `design/` workflow)._
   - ✅ **DEPLOYED (2026-06-18) — Coolify dev instance.** Image **built and running** at
     **https://vmentorydev.edgestudios.co.za**, verified serving (HTTPS via Coolify/Traefik, `/health`
     200, SPA 200, auth 401/200). Reverse-proxy mode: Traefik terminates TLS; app runs
     `VMENTORY_HTTP_ONLY=1` on HTTP 8443; SQLite on a `/data` named volume. Initially accessed via
     `VMENTORY_TOKEN` (interim auth); **that token was retired in slice (2)** — access is now via
     cookie-based login. **Post-deploy fix (`ca196f4`):** non-root (uid 10001) crash-loop (exit 139) from
     `ErrorLogger` writing `errors.log` into root-owned `/app` → resolved to a writable path
     (`VMENTORY_LOG` / `/data` / app base) + made fail-safe (lesson: **non-root containers write only to
     mounted volumes**). **Deploy lives in a separate private `claude-ops` repo** (`deploy` skill,
     `ssh edgestudios`; token/UUIDs server-side in `/etc/coolify-deploy/config.json`) — **not in this
     clone**, no secrets copied here. **Live operational fact:** the container **cannot inventory
     Hyper-V** (no PowerShell/WinRM + no LAN line-of-sight; every HV host "unreachable" — by design,
     the ENG-0011 trigger; see §3 "Live operational facts" + §4).
   - ✅ **Re-baselined slice (2) (login + RBAC, ENG-0008) done & verified (2026-06-18):** cookie-based
     session auth (HttpOnly Secure SameSite=Strict, 12h sliding), `PasswordHasher` (PBKDF2-SHA256,
     BCL-only, work-factor in hash), EF Core `AppUserEntity` + `AuditEventEntity` (`AddAuth` migration),
     `IUserStore` / `EfUserStore`, `RbacCatalog` (static role→`ProviderCapability`+`ConsolePermission`
     mapping). Endpoints: `POST /api/auth/login`, `GET /api/auth/me`, `POST /api/auth/logout`,
     `POST /api/auth/change-password`. Authz chokepoint middleware replaces the old token middleware —
     gates all `/api/*`, passes `/api/auth/*`, enforces `MustChangePassword` (returns 403 +
     `requiresPasswordChange:true` until changed). First-admin seed on startup:
     `VMENTORY_ADMIN_USER`/`VMENTORY_ADMIN_PASSWORD` env (default username `admin`; if no password,
     auto-generates + prints to stdout for Coolify log capture); always seeded with `MustChangePassword=true`.
     `VMENTORY_TOKEN` / session-token interim auth **retired** (removed from `AppConfig` + all routes).
     SSE (`/api/events`) now uses cookie auth (EventSource sends cookies on same-origin GET; `?token=`
     removed). Minimal login + change-password walls in `wwwroot/index.html` (functional, unstyled);
     design request raised at `design/requests/from-codebase/2026-06-18-login-screen.md`.
     Mock mode: any credentials accepted (no DB), returns Admin role. **Verified:** build 0/0;
     mock-mode smoke test: unauthenticated `/api/state` → 401; login → 200 + cookie;
     `/api/auth/me` → `{username,role}`; authenticated `/api/state` → 200 (5 mock hosts);
     logout → cookie cleared; `/api/state` post-logout → 401.
     **Carry-overs:** login UI awaiting design; `/api/quit` + Updater re-scope still pending.
   - ✅ **Re-baselined slice (3) (`ISecretStore`, ENG-0002) done & verified (2026-06-18):** `ISecretStore`
     interface + two implementations: `AesGcmSecretStore` (scoped, SQLite-backed, AES-256-GCM envelope
     encryption) and `EphemeralSecretStore` (singleton, in-memory, used when `VMENTORY_KEK` is absent).
     New persistence entities: `SecretEntity` (encrypted blob + nonce) + `DekEntity` (wrapped DEK);
     `AddSecrets` EF migration. `DekProvider` (singleton): caches the plaintext DEK in memory after
     first load; creates + wraps on first run; unwraps on subsequent restarts using the KEK.
     `VMENTORY_KEK` env var (base64, 32 bytes): if set, credentials and future secrets are AES-256-GCM
     encrypted in SQLite and survive restarts; if absent, `EphemeralSecretStore` is used (ephemeral,
     Phase-1 behavior). Startup banner now shows secret-store mode. Startup `LoadPersistedCredsAsync`
     restores global WinRM creds + per-host creds into `Store` from the secret store on start.
     `POST /api/credentials` now persists via `ISecretStore`. `POST /api/hosts` (per-host creds) and
     `DELETE /api/hosts/{id}` both call `ISecretStore` to set/delete the `host_cred:{id}` entry.
     **Verified:** build 0/0; mock-mode smoke test: `/health` 200, unauthenticated `/api/state` → 401,
     login → 200 + cookie, `/api/auth/me` → `{username,role}`, authenticated `/api/state` → 200.
     **Carry-overs:** login UI awaiting design; `/api/quit` + Updater re-scope still pending.
   - ✅ **Re-baselined slice (4) (Proxmox API read provider, ENG-0009) done (2026-06-18, commit `2907fd4`):**
     `ProxmoxProvider` (`IVirtualizationProvider` for Proxmox VE) drives the PVE REST API —
     `/api2/json/version`, `/api2/json/nodes`, `/nodes/{n}/status`, `/nodes/{n}/qemu`,
     `/nodes/{n}/lxc`. Advertises `Inventory | LiveStats`. Auth via `PVEAPIToken` HTTP header; token
     stored as a per-host credential in `ISecretStore` (password field). TLS: accepts self-signed when
     `host.SkipTlsVerification` is set. `ProviderRegistry` routes `IVirtualizationProvider` calls by
     `PlatformKind`; both providers registered as singletons. `Host` model + `HostRegistrationEntity` +
     `EfInventoryStore` all gained `SkipTlsVerification bool`; `AddProxmox` EF migration adds the
     column. `Poller` rewritten: Proxmox branch checks port 8006 + calls `QuickConnectAsync` every 30 s;
     HyperV branch unchanged. `Program.cs` registers both providers + `ProviderRegistry`; `AddHostsDto`
     gains `Platform`, `Token`, `SkipTlsVerification`; `/api/hosts` routes by platform; `/api/scan`
     calls `registry.For(host.Platform).ScanAsync(host)`. SPA add-host modal: platform selector
     (HyperV/Proxmox radio), conditional HV-creds vs PVE token + skip-TLS fields; host rows: HV/PVE
     badge, platform-aware port label (`TCP 5985` vs `API :8006`) and health tip.
     **Operational gotchas for Proxmox:**
     - **One registered Host = one PVE node.** `ProxmoxProvider` calls `/nodes/{n}/qemu` and `lxc`
       scoped to the node name returned by `/api2/json/nodes`. A Proxmox cluster with multiple nodes
       requires one Host registration per node today. Multi-node cluster support (one Host per cluster
       via `/cluster/resources`) is a pending enhancement for slice (5)+.
     - **`SkipTlsVerification` is required for self-signed PVE certs** (the default on most PVE
       installs). Set it at registration time; Core will use `HttpClientHandler.ServerCertificateCustomValidationCallback`
       to bypass chain validation only for that host's HTTP client.
     - **Token format (verified live 2026-06-18 against vega14):** store the **BARE** token —
       `<user>@<realm>!<tokenid>=<uuid>` (e.g. `root@pam!vmentory1=<uuid>`) — in `ISecretStore` as the
       per-host cred password field. **Do NOT include the `PVEAPIToken=` prefix:** `ProxmoxProvider`
       prepends it when building the `Authorization` header ([`ProxmoxProvider.cs:123`](../../ProxmoxProvider.cs)),
       so storing the prefixed form produces `Authorization: PVEAPIToken=PVEAPIToken=…` → **401**. The SPA
       add-host placeholder already shows the correct bare form (`user@pam!tokenid=…`). Note the
       `<user>@<realm>!<tokenid>` must match an **existing** PVE token exactly — a wrong realm or token id
       gives **401 "Authentication failed!"** (vs **403** for a valid-but-unprivileged token).
     **Carry-overs (unchanged from slice (3)):** login UI awaiting design; `/api/quit` + Updater re-scope
     still pending.
   - **Next:** slice (5) **Proxmox SSH executor + management/Deploy write verbs** (capability-gated,
     RBAC-enforced, audited). Then the **HV-scoped agent + private CA** (6) and HV→PVE migration (7).
     **The agent is still not next** (demoted + HV-scoped, ENG-0009).
3. Owner reviews the five specs (`docs/phase2/specs/`) and the four design mockups (`design/`).
4. Reconcile the migration docs (virt-v2v → qm-importdisk, fold in the skill).

## 6. How to resume on a new machine
```
git clone <repo> && cd VMentory && git checkout dev
```
Read in this order: this file → `ARCHITECTURE.md` → `ROADMAP.md` → `docs/engineering/REGISTER.md`
→ the `specs/` you need. The three agents are defined in `.claude/agents/` and can be invoked by
name. `CLAUDE.md` describes the current codebase (Phase 2 foundation): file map, API surface,
gotchas, env contract, and dev commands — all updated to reflect the built slices.

> Note: agent **memories** live outside the repo (in the local Claude profile) and do **not** travel
> with the clone — this doc is the portable source of truth.
