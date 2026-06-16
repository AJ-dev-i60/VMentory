# ENG-0008 — RBAC / scoped roles for the web console

**Status:** Open
**Raised:** 2026-06-16 by human (owner)
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

## Recommendation
_Pending engineering-agent analysis._ Likely **start with B's permission catalog but expose a few
fixed default roles (A) on top**, so the common case is one click and the flexible case exists; keep
identity pluggable (local now, OIDC seam later, consistent with the "optional OIDC" note in ROADMAP 2.2).

## Open sub-questions
- Reuse `ProviderCapability` as the permission taxonomy, or a separate console-permission enum? (Verbs
  map cleanly, but console permissions also cover non-provider actions — credentials, enrollment, audit.)
- Per-host / host-group scoping, or org-wide only, in the first cut?
- Interaction with the **audit log** (ENG-0006 cross-cutting): every allow/deny decision auditable.
- Sequencing: minimal admin login lands in **2.0** (replacing the session token); does basic RBAC land
  with **2.2 auth hardening**, or wait until Backup (2.6) makes the backup-operator role meaningful?

## Decision
> _Pending._
