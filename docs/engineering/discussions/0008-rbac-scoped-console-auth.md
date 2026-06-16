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
