# ENG-0012 — Credential management revamp (storage model + UX)

**Status:** Open / Raised (2026-06-19) — discuss + plan, **not decided**.
**Owner:** human.
**Raised by:** owner, during the first live Proxmox onboarding (vega14).

## Trigger

Onboarding vega14 surfaced two credential pain points:

1. **Token entry is unintuitive.** The PVE API token is a compound string
   (`user@realm!tokenid=secret`) that reads like a connection string. The owner
   pasted only the secret UUID (`aa8f8e48-…`) and every connect failed with no
   clear reason. The format was not stated anywhere in the UI. *(Mitigated for
   now — slice-4 follow-up added an inline format hint + client-side validation
   in the add-host modal, and the PROGRESS/CLAUDE docs were corrected. This is a
   band-aid, not the revamp.)*
2. **Per-host credential entry inside the add-host flow is bulky.** Credentials
   are entered ad-hoc per host (HV username/password or PVE token) with no way to
   define a credential once and reuse it across hosts, rotate it, or see what is
   stored.

## Scope to discuss

This is both an **engineering** and a **UI/UX** topic:

- **Engineering / data model.** Promote credentials to first-class, named,
  reusable entities in `ISecretStore` (ENG-0002) — a "credential" or "connection
  profile" a host *references* rather than embeds. Covers: HV username/password,
  PVE API tokens, and the forthcoming ENG-0009 SSH key. Rotation, revocation,
  "where is this used", and audit (ties to ENG-0008 chokepoint). Decide whether
  this is a new entity + endpoints (`/api/credentials/*` CRUD) vs. the current
  global-creds + per-host-creds split.
- **UI/UX.** A dedicated credential-management surface (list / add / rotate /
  delete, "used by N hosts"), and an add-host flow that *picks* an existing
  credential instead of re-typing. Structured PVE-token entry (separate
  user@realm / tokenid / secret fields, recombined for storage) to kill the
  connection-string confusion entirely. → hand to the **ui-design** agent for a
  mockup + spec; the band-aid hint/validation is the interim.

## Dependencies / ties

- ENG-0002 (`ISecretStore`) — the storage substrate already exists.
- ENG-0008 (RBAC chokepoint) — credential CRUD is a privileged, audited surface.
- ENG-0009 (Proxmox SSH key) — slice (5) adds a *second* per-host secret (SSH
  key alongside the API token); a good revamp anticipates multiple secret types
  per host. Worth resolving the model **before** slice (5) piles on a second
  ad-hoc secret field.

## Not yet decided

Everything. This row exists to capture the trigger so the engineering + UI agents
can pick it up. No implementation beyond the interim hint/validation.
