# ENG-0008 — RBAC / scoped roles for the web console

**Status:** Decided (2026-06-16) — **fixed roles over a pillar×verb catalog; firm + early (login in the ENG-0010 deployment slice; roles before the first write verbs)**
**Raised:** 2026-06-16 by human (owner)
**Decided:** 2026-06-16 by operator (owner)
**Affects:** ARCHITECTURE.md §Security (user auth), ROADMAP 2.2 (auth hardening — currently "roles later"), persistence schema (roles/permissions), every pillar's write verbs (Migrate/Deploy/Backup/lifecycle)
**Related:** ENG-0002 (secret store), ENG-0006/0007 (pillars & scope), ENG-0004 (constrained-verb agent — the *agent* authz boundary, distinct from *console-user* authz)

## Context

Phase 2 turns VMentory into a **hosted web console** (no longer loopback + single session token —
ARCHITECTURE.md §Security). The owner wants **scoped, role-based access** for console users so a
given operator only sees/does what their role allows — distinct from, and layered on top of, the
agent's constrained-verb executor (ENG-0004, which constrains what *Core* can ask an *agent* to do,
not what a *human* can ask *Core* to do).

This is **single-operator at the tenancy level** (ENG-0006 — no `tenant_id`), but **multi-user with
differentiated permissions** within that one operator/org. The two are not in conflict: one tenant,
several human roles.

The owner's illustrative roles (to be refined, not final):
- **Backup operator** — backup/restore actions; **cannot manage VMs** (no lifecycle, no migrate, no deploy).
- **VM operator** — view + limited actions (e.g. **view backups and "retry"** a backup, but not configure/delete them); presumably VM lifecycle within scope.
- (implied) an **admin/full** role.

Permissions likely want to be **per-pillar × per-action** (e.g. Observe:view, Migrate:run,
Backup:run/retry/configure, Deploy:provision, Lifecycle:start/stop/reconfigure), which maps naturally
onto the **ProviderCapability** verb taxonomy already defined in
[VMentory.Core/ProviderCapability.cs] — worth reusing rather than inventing a parallel permission set.

## The question

What is the console-user **authn + authz** model? Specifically: role model (fixed roles vs custom
roles/permission sets), permission granularity (per-pillar, per-verb, per-host/host-group?),
identity source (local accounts vs OIDC/SSO — ARCHITECTURE already floats OIDC as optional), and where
roles/permissions persist and are enforced (single chokepoint in Web/API + the operations engine).

## Options
_To be analyzed by the engineering agent. Sketch only:_
- **A — Fixed built-in roles** (Admin / VM-operator / Backup-operator / Viewer): simplest, ships fast, less flexible.
- **B — Custom roles over a permission catalog** (permissions = pillar×verb, possibly reusing `ProviderCapability`): flexible, more schema + UI.
- **C — Defer to an external IdP's groups/claims** (OIDC) and map claims→permissions: offloads identity, needs an IdP.

## Recommendation (PROPOSED — awaiting operator approval)

**Model: A-with-a-B-seam.** Ship **fixed built-in roles** (Admin / VM-operator / Backup-operator /
Viewer) backed by an **internal permission catalog keyed on pillar×verb**, so the role→permission
mapping is data, not hardcoded `if`s, and a custom-roles UI (B) can be added later without reworking
enforcement. **Do not** ship custom-role authoring in release 1 — for a single operator with a handful
of users it is schema + UI cost with no payoff yet.

**Permission taxonomy:** reuse the **`ProviderCapability`** verb flags
([VMentory.Core/ProviderCapability.cs](../../../VMentory.Core/ProviderCapability.cs)) as the *provider-
action* axis (Start/Stop/Reconfigure/Provision/Backup/…), and add a **small console-only permission set**
for non-provider actions (manage-credentials, manage-enrollment, view-audit, manage-users). One catalog,
two sources — avoids inventing a parallel provider-permission enum while still covering console actions.

**Identity:** **local accounts now** (hashed credential in the persisted store, KEK-wrapped per ENG-0002),
with an **OIDC/claims-mapping seam later** (Option C as a future provider behind the same authz chokepoint).

**Enforcement:** a **single authorization chokepoint** in `VMentory.Web`/API **and** in the operations
engine (so a job dispatched by API and a job resumed by the engine honor the same check), every
allow/deny **audited** (ENG-0006 cross-cutting).

**Sequencing — the change the re-baseline forces (was the open question below):** RBAC is now a **FIRM
requirement** (operator's locked frame: "the web UI needs login with RBAC"), not "roles later." It
**gates web-first multi-user access**, so it lands **early**, not at 2.6-Backup. Concretely:
1. **Minimal admin login** replaces the session token in the **containerized-Core / deployment slice**
   (ENG-0010) — the very first re-baselined foundation slice that exposes a hosted UI.
2. **Fixed roles + the pillar×verb catalog + the authz chokepoint** land **before any write verbs are
   exposed** — i.e. with/just-before Proxmox management/Deploy, **not** deferred to 2.2 "auth hardening"
   and certainly not to 2.6. (Backup-operator role simply has no Backup verbs to grant until pillar 4
   ships — but the *framework* is in place from the first write-capable release.)

**Trade-off accepted:** fixed roles are less flexible than full custom-role authoring; we accept that
because the single-operator/few-users reality makes four well-chosen roles + a catalog-backed seam the
right cost/benefit, and the seam keeps custom roles a non-breaking later addition.

## Open sub-questions
- Reuse `ProviderCapability` as the permission taxonomy, or a separate console-permission enum? (Verbs
  map cleanly, but console permissions also cover non-provider actions — credentials, enrollment, audit.)
- Per-host / host-group scoping, or org-wide only, in the first cut?
- Interaction with the **audit log** (ENG-0006 cross-cutting): every allow/deny decision auditable.
- ~~Sequencing: minimal admin login lands in **2.0**; basic RBAC with 2.2 or wait for 2.6?~~ →
  **Resolved by the recommendation above:** login lands with the containerized-Core slice (ENG-0010),
  the role framework before the first write verbs. (PROPOSED.)
- Per-host/host-group scoping: **org-wide only in the first cut** recommended; per-host scoping is a
  later catalog extension. (Confirm.)
- Reuse `ProviderCapability` + a small console-permission set (recommended above) vs a single unified
  enum — confirm the two-source catalog is acceptable.

## Decision

**2026-06-16 · Operator (owner) approved the recommendation, firm and early.** The console
authn/authz model for release 1 is:

- **Roles:** **fixed built-in roles** (Admin / VM-operator / Backup-operator / Viewer) backed by an
  **internal permission catalog keyed on pillar×verb** (role→permission mapping is data, not hardcoded
  `if`s). **No custom-role authoring in release 1**; a custom-roles UI (Option B) is a non-breaking
  later addition behind the same enforcement.
- **Permission taxonomy:** **reuse `ProviderCapability`**
  ([VMentory.Core/ProviderCapability.cs](../../../VMentory.Core/ProviderCapability.cs)) as the
  provider-action axis, plus a **small console-only permission set** (manage-credentials,
  manage-enrollment, view-audit, manage-users). Two-source catalog confirmed acceptable.
- **Identity:** **local accounts now** (hashed credential, KEK-wrapped per ENG-0002) with an
  **OIDC/claims-mapping seam later** behind the same authz chokepoint.
- **Enforcement:** a **single audited authorization chokepoint** in `VMentory.Web`/API **and** the
  operations engine; this chokepoint **must exist before any write verb is exposed.**
- **Sequencing (firm + early):** **minimal admin login lands in the ENG-0010 deployment slice** (the
  first hosted-UI slice, replacing the session token). **Fixed roles + the pillar×verb catalog + the
  authz chokepoint land before any write verbs are exposed** — i.e. with/just-before Proxmox
  management/Deploy, **NOT** deferred to 2.2 "auth hardening" or 2.6-Backup. Backup-operator simply has
  no Backup verbs to grant until pillar 4 ships, but the framework exists from the first write-capable
  release.

### Open-sub-question resolutions
- **Per-host / host-group scoping:** **org-wide only in the first cut**; per-host scoping is a later
  catalog extension.
- **Audit:** every allow/deny decision is auditable (ENG-0006 cross-cutting).

Feeds the documentation agent: ARCHITECTURE §Security, ROADMAP auth sequencing, persistence schema
(roles/permissions/local accounts), and the ENG-0010 deployment slice.

## Implementation sub-questions (for the slice-(2) build)

> **The decision above stands** — this section records **open implementation specifics**, not new
> decisions, for the slice-(2) (login + RBAC) planning session to resolve. Slice (1) (containerized
> Core + hosted bootstrap, ENG-0010) is built and deployed (dev at `vmentorydev.edgestudios.co.za`),
> so these must be answered against the **headless/containerized runtime** the foundation now is —
> no interactive desktop, env-injected config (ENG-0010), stdout captured by Coolify.

1. **First-admin bootstrap for a headless container.** There is no interactive first-run setup; the
   container boots unattended. How is the initial **Admin** account created? Options to weigh at build
   time (each ties to the ENG-0010 env-injection contract, cf. `Program.cs:38` `EnvOr(...)`):
   - **Env-provided initial admin** (e.g. `VMENTORY_ADMIN_USER` / `VMENTORY_ADMIN_PASSWORD`) seeded on
     first boot, with **forced password rotation at first login**. *Trade-off:* simplest for a single
     operator and fits the env contract, but a plaintext password sits in the orchestrator's env/secret
     store until rotated, and the seed path needs an idempotency rule (seed only when no users exist).
   - **One-time first-run setup token** printed to **container logs** (Coolify captures stdout), redeemed
     once via the login UI to set the first admin. *Trade-off:* no standing password in env, but log
     access becomes a trust boundary and the token must be single-use + expiring; awkward if logs are
     shared/forwarded.
   - **CLI / seed step** (a `dotnet`/container `exec` subcommand that creates the admin). *Trade-off:*
     explicit and auditable, but adds an operational step the "install nothing but the container" framing
     (CLAUDE.md) tries to avoid, and is clumsy on a managed PaaS like Coolify.
   - *Cross-cut:* whichever is chosen must define the **"no users yet" state** of the authz chokepoint
     (does the UI hard-redirect to a bootstrap flow, or is the app inert until seeded?).

2. **Session mechanism.** Cookie-based session vs bearer token, and how it **cleanly replaces the
   interim token middleware**. Today a single static token gates everything: the global middleware at
   `Program.cs:147-155` (`X-Session-Token` header or `?token=` query) and a **separate re-check on SSE
   `/api/events` at `Program.cs:508-510`; the `VMENTORY_TOKEN` env stopgap is `Program.cs:38-40`.
   Open points:
   - **Cookie session** (HttpOnly, Secure, SameSite) vs **bearer token** (Authorization header). Cookie
     fits a browser SPA and the SSE stream (EventSource can't set headers — today SSE relies on the
     `?token=` query param, which leaks into logs/URLs); bearer is cleaner for a future API/CLI but needs
     a custom SSE carrier. *Trade-off to surface:* the SSE auth path is the awkward case either way.
   - **CSRF posture** if cookies are used (state-changing `/api/*` verbs need anti-CSRF; bearer-in-header
     sidesteps CSRF but reopens the SSE-header problem).
   - **Clean replacement:** retire `VMENTORY_TOKEN` + the two token checks **together** so there's no
     half-authenticated window; decide whether `--mock`/dev keeps a bypass.

3. **Password storage / hashing — and the ENG-0002 boundary.** Algorithm choice (e.g. **PBKDF2** [in
   the BCL, no new dependency] vs **Argon2id** [stronger, needs a package — weigh against the offline-
   restore constraints noted in CLAUDE.md gotcha #3/#5]) and **per-user salt + tunable work factor**.
   Key boundary to clarify in the build: **password hashes are NOT "secrets at rest" in the ENG-0002 KEK
   sense.** `ISecretStore` (envelope-encrypted, KEK-wrapped) is **slice (3) — AFTER login**, so slice-(2)
   login **cannot depend on it**. A one-way password **hash** is a verifier, not a recoverable secret, so
   it can live as a column in the existing EF Core store (`VMentory.Core/Persistence/`) without the KEK.
   *Open:* confirm hashes-as-plain-column is acceptable pre-`ISecretStore`, and note the seam if any
   *recoverable* per-user credential (e.g. an OIDC client secret later) ever needs the KEK once slice (3)
   lands. The Decision's phrase "hashed credential, KEK-wrapped per ENG-0002" should be read as the
   *long-term* posture, not a slice-(2) blocker — flag this wording for the planning session.

4. **Authz chokepoint placement.** A **single** middleware/filter must cover Web/API **and** the future
   ops engine (the Decision's "single audited chokepoint before any write verb"). Open specifics:
   - **Where it sits** relative to the current token middleware (`Program.cs:147`) — replace it in place,
     or layer authn (who) then authz (may-do-verb) as two stages?
   - **Role → capability mapping:** how fixed roles resolve to the `ProviderCapability` pillar×verb
     catalog (`VMentory.Core/ProviderCapability.cs`) **plus** the small console-only permission set
     (manage-credentials, manage-enrollment, view-audit, manage-users). Where does the mapping table
     live — data in the EF store, or a static seeded catalog?
   - **Audit hook:** every allow/deny emits an event. This ties **ENG-0011** (observability/logging) and
     its **`audit_event`** table idea (persistence-and-security §6); ENG-0011 is still Open, so slice (2)
     should define the **minimal audit write** (who/verb/allow-deny/correlation-id) without waiting on the
     full logging design — and mint the **correlation_id at the chokepoint** (the open question in
     ENG-0011 §"Audit/ops correlation key").
   - *Note:* in slice (2) there are **no write verbs exposed yet** (Proxmox read/management comes later),
     so the chokepoint can be **built and tested ahead of** the verbs it will guard — it just needs the
     seam in place before the first write verb ships, per the Decision.

5. **Login UI dependency (design workflow).** The login screen touches `wwwroot/index.html`, which is
   owned by the **`design/` workflow** (CLAUDE.md "UI/UX work — read this first"). To avoid blocking:
   **backend/API (authn endpoints, session, chokepoint) lands first**; the login UI goes through
   `design/` (raise a `design/requests/from-codebase/` item for the login screen + first-admin/rotation
   flow). Open: confirm a **minimal/unstyled interim login** is acceptable to unblock end-to-end testing
   while the designed screen is produced, or whether the API ships dark until the UI is ready.
