# Design request — Credential management surface + structured token entry

**From:** codebase · **Date:** 2026-06-19 · **Priority:** medium (future)
**Related:** ENG-0012 (`docs/engineering/discussions/0012-credential-management-ux.md`)

> **Unblocked 2026-06-19 — ENG-0012 engineering half is now Decided.** The storage/
> data model this request assumed (named, reusable `CredentialEntity` + CRUD +
> rotation/audit) is locked: a host references credentials via two typed slots
> (`ManagementCredentialId` + `TransportCredentialId`), global creds are retired (a
> host *always* picks a named credential), and the vault is **Admin-managed** with a
> view-only metadata flag for VmOperators. **Design implications:** (a) the add-host
> "pick a credential" dropdown is the *only* way to set creds now — there is no
> "use global" toggle to design around; (b) a VmOperator adding a host can *pick*
> but not *create/rotate/delete* credentials, so the "+ new credential" inline
> escape hatch is Admin-only (VmOperators see pick-only); (c) structured PVE-token
> fields (`user@realm` / `tokenid` / `secret`) map exactly onto the stored model —
> `user@realm`+`tokenid` are non-secret metadata, only `secret` is vaulted — and
> CRUD never reveals a stored secret (rotate is write-only, no "show secret").
> Decision: `docs/engineering/discussions/0012-credential-management-ux.md`.

## Context / trigger

First live Proxmox onboarding (vega14). Two UX problems surfaced:

1. The PVE **API token** is a compound string `user@realm!tokenid=secret` that
   reads like a connection string. The owner entered only the secret UUID and
   every connect failed with no clear reason. *(Interim band-aid already shipped
   in `wwwroot/index.html`: an inline format hint under the token field + client-
   side validation that catches a bare-UUID paste. This request is the real fix.)*
2. Entering credentials **per host, inside the add-host modal**, is bulky. There
   is no way to define a credential once and reuse it, rotate it, or see what is
   stored / where it is used.

## What to design (not implementation — mockup + spec)

1. **A dedicated credential-management screen.** List of named credentials
   (HV username/password, PVE API token, and the upcoming ENG-0009 SSH key),
   each showing type, "used by N hosts", and rotate/delete actions. Add/edit
   form per credential type.
2. **Add-host flow picks an existing credential** instead of re-typing — a
   dropdown of compatible credentials for the selected platform, with an
   "+ new credential" inline escape hatch.
3. **Structured PVE-token entry** — separate fields for `user@realm`, `tokenid`,
   and `secret`, recombined into `user@realm!tokenid=secret` on save, so the
   connection-string confusion is designed out entirely.

## Out of scope

- The storage/data model (named credential entities in `ISecretStore`, CRUD
  endpoints, rotation/audit) — that's ENG-0012's engineering half; design assumes
  it exists.
- The live `wwwroot/index.html` interim hint/validation stays until this ships.

## Notes

Worth sequencing **before/with slice (5)**, which adds the Proxmox SSH key as a
*second* per-host secret — a host will soon reference more than one credential,
which strengthens the case for first-class reusable credentials over embedded fields.
