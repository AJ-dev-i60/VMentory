# ENG-0012 — Credential management revamp (storage model + UX)

**Status:** **Decided (2026-06-19)** — named, reusable `CredentialEntity` (flat TPH table + JSON
`Descriptor` + closed C# union), global-creds retired, typed credential slots on `Host`, Admin-only
vault. Full decision at the bottom of this file.
**Owner:** human.
**Raised by:** owner, during the first live Proxmox onboarding (vega14).
**Decided:** 2026-06-19 by operator (owner).
**Affects:** `VMentory.Core/Persistence/*` (new `CredentialEntity` + EF migration + startup
promotion task), `VMentory.Core/Models.cs` (`Host` slots), `ISecretStore` key convention,
`RbacCatalog.cs` + `ConsolePermission`, `Program.cs` credential CRUD + add-host/PATCH-host,
`wwwroot/index.html` (credential UI — design via `design/requests/from-codebase/2026-06-19-credential-management.md`),
ENG-0002/0008/0009.

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

## Forks analyzed

The revamp turned out to be six near-orthogonal decisions, each with genuinely
distinct options. They are recorded together so the slice is implementation-ready.

### Entity shape — how a credential is persisted

Today there is no credential entity at all: secrets are raw blobs in `ISecretStore`
under ad-hoc keys (`global_winrm`, `host_cred:{hostId}` — `SecretEntity.cs:8`,
`Program.cs:387,446,612,670`), and the host carries a `UseGlobalCreds` bool
(`Models.cs:81`, `HostRegistrationEntity.cs:13`). A credential is not a thing you
can name, list, rotate, or count references on. The fork is how to make it one.

- **A — flat single-table, discriminator only.** One `CredentialEntity` with
  `Kind`/`Platform` columns and loose nullable fields (or a single JSON blob) for
  the per-kind shape; no typed C# union.
  - *Pros:* simplest schema; one table; trivial EF mapping.
  - *Accepts:* the consume side stays stringly-typed — when slice-5 adds the SSH
    key kind there is **no compiler check** that every `switch` over kinds was
    updated. Exhaustiveness is lost exactly when a third kind lands.
- **B — typed-per-platform tables (TPT inheritance).** `HyperVCredential`,
  `ProxmoxTokenCredential`, `ProxmoxSshCredential` as an EF inheritance hierarchy.
  - *Pros:* each kind is a real type with its own columns; DB-level shape per kind.
  - *Accepts:* this would be the **first inheritance hierarchy in the persistence
    layer** (everything else is flat entities). TPT carries multi-table read joins
    and FK awkwardness for a domain that is fundamentally "a small tagged record."
    Over-structures the storage for what is really a closed union of ~3 shapes.
- **C — flat TPH table + JSON `Descriptor` column + a closed C# record union
  (chosen).** One `CredentialEntity` (`Id`, `Name`, `Kind`, `Platform`, `Descriptor`
  JSON, audit columns), with a closed `abstract record CredentialDescriptor`
  union (`HyperVPassword`, `ProxmoxToken`, `ProxmoxSshKey`) as the typed parse/
  consume boundary. **Secret material never lands in the table** — only non-secret
  descriptor fields do; secrets stay in `ISecretStore` (A2 below).
  - *Pros:* single flat table (matches the rest of persistence, easy EF mapping,
    cheap list/count queries straight off columns) **and** a typed boundary the
    compiler enforces — a `switch` expression over the closed union is exhaustive,
    so adding the slice-5 SSH kind is a compile error everywhere it must be handled.
    Best of both: flat read model, typed write/consume model.
  - *Accepts:* a serialize/deserialize seam between the JSON column and the record
    union (a small parse function and a round-trip test), and the discipline that
    the JSON only ever holds **non-secret** descriptor fields.

**Chosen: C.** It keeps storage flat and queryable while restoring exhaustiveness
at the one boundary that matters (consuming a credential to build a provider client),
and it is the only option that makes slice-5's SSH kind a *typed* addition rather
than another ad-hoc field.

### Fork A — where the secret lives vs the metadata

- **A1 — everything in the vault blob.** The whole credential (name, kind, platform,
  *and* secret) serialized into one `ISecretStore` value.
  - *Accepts:* every list/"used by N"/metadata read must `GetAsync` + decrypt the
    secret just to render a name. The vault becomes the query surface; you decrypt
    to count. Pushes secret material through every metadata code path.
- **A2 — split storage (chosen).** Non-secret fields live as real columns / in the
  `Descriptor` JSON on `CredentialEntity`; **only the secret material** lives in
  `ISecretStore`.
  - *Pros:* list/GET project straight from columns — **no decryption to render
    metadata**, and the secret-never-leaves-server rule (below) becomes structural:
    the metadata path literally has no vault call. Cheap "used by N hosts" via a
    join. Rotation touches only the vault entry.
  - *Accepts:* two stores to keep consistent (a row + its vault key(s)); create/
    delete must be transactional-ish across both (write vault first, then row;
    delete row first, then vault — with the startup promotion and delete paths
    tolerant of a half-state).

**Chosen: A2.** LOCKED.

### Fork B — deleting a referenced credential

- **B1 — restrict while referenced (chosen).** `DELETE /api/credentials/{id}`
  returns **409 Conflict** with a `usedByHosts` list when any `Host` slot
  references it; delete only succeeds when reference count is zero.
  - *Pros:* no host is ever silently left with a dangling slot; the operator sees
    exactly what to detach first. Matches B1-style referential safety used
    elsewhere (deleting a host clears its secret, `Program.cs:612`).
  - *Accepts:* an extra "where used" query on delete, and the operator must detach/
    repoint hosts before deleting a shared credential (a deliberate guard rail).
- **B2 — cascade / orphan.** Delete the credential and null the slots (or cascade).
  - *Accepts:* hosts silently lose their management/transport credential and go
    unreachable on next poll with no obvious cause — the exact "no clear reason"
    failure mode ENG-0012 was raised to kill.

**Chosen: B1.** LOCKED.

### Fork C — global credentials

- **C1 — retire global creds entirely (chosen).** Remove `POST /api/credentials`
  (the global-WinRM setter, `Program.cs:387`), `Host.UseGlobalCreds`
  (`Models.cs:81`, `HostRegistrationEntity.cs:13`, `Store.cs:32`), and the
  `global_winrm` vault key. The old global WinRM credential is **migrated into a
  named `CredentialEntity`** ("Global WinRM" or similar) that hosts reference like
  any other.
  - *Pros:* one credential model, not two (named entities *and* a magic global).
    The "define once, reuse across hosts" goal is met by named credentials directly
    — global was just an un-named, un-rotatable special case of reuse. Simpler
    add-host/PATCH logic (the `UseGlobalCreds` branching at `Program.cs:422,428,
    644-656` collapses to "pick a credential id").
  - *Accepts:* a one-time data migration (see sub-decision 2) and the loss of the
    implicit "new HV host inherits global creds" default — the operator now picks a
    credential when adding a host (the named "Global WinRM" cred is right there in
    the dropdown, so the friction is a single selection).
- **C2 — keep a default-per-platform flag.** Mark one credential per platform as the
  default so add-host can pre-select it.
  - *Accepts:* re-introduces a "magic default" concept; can be added later as a
    pure UX fast-follow (a `IsDefault` column + pre-selected dropdown) **without**
    schema rework, since named credentials already exist.

**Chosen: C1**, with C2 explicitly available as a fast-follow if the manual pick
proves annoying.

### Fork D — how a host references credentials

- **D1 — single credential FK on `Host`.**
  - *Accepts:* cannot express Proxmox needing **two** secrets at once (API token for
    the control plane **and** the ENG-0009 SSH key for disk-import/guest-edit). The
    slice-5 second secret would force a schema change immediately.
- **D2 — typed credential slots (chosen).** Two nullable FK columns on `Host`:
  `ManagementCredentialId` (the control-plane credential: HV WinRM, or PVE API token)
  and `TransportCredentialId` (the data/disk-op credential: **null for Hyper-V**;
  the **Proxmox SSH key** for the slice-5 disk-import/guest-edit residue per ENG-0009).
  - *Pros:* names the two real roles a host can need; Proxmox's two-secret reality
    (ENG-0009 decision) is expressed without a generic join table; HV simply leaves
    `TransportCredentialId` null. The provider seam reads exactly the slot it needs.
  - *Accepts:* two columns instead of one, and the (correct) assertion that a host
    needs at most two credential roles. If a future platform needed a third role,
    that is a third named slot, not an M:N table.
- **D3 — generic credential↔host M:N with a `role` column.**
  - *Accepts:* over-engineered for a fixed, small, well-known set of roles; a join
    table and role enum to maintain for what D2 expresses in two typed columns.

**Chosen: D2.** LOCKED.

### RBAC — who manages the vault

Today `ConsolePermission.ManageCredentials` is granted to **both** Admin and
VmOperator (`RbacCatalog.cs:14-16`). With credentials becoming a first-class,
rotatable, "where-used" surface holding the keys to every host, blanket
VmOperator write access is too broad.

- **Chosen — Admin-only vault, with a metadata-view flag for operators.** Split the
  single flag into two:
  - `ConsolePermission.ManageCredentials` → **Admin only** (create / rotate / delete).
  - **new** `ConsolePermission.ViewCredentials` → granted to **Admin + VmOperator**
    (list **metadata only** — name/kind/platform/used-by — so an operator can pick a
    credential when adding a host, but cannot create, rotate, reveal, or delete).
  - *Accepts:* one more flag and a small `RbacCatalog` change; VmOperators lose the
    ability to define credentials (they consume Admin-curated ones). This matches the
    ENG-0008 intent that credential CRUD is a privileged, audited chokepoint.

  Resulting `RbacCatalog` console mapping:
  - Admin: `ViewAudit | ManageCredentials | ViewCredentials | ManageEnrollment | ManageUsers`
  - VmOperator: `ViewCredentials` (only — drops `ManageCredentials`)
  - BackupOperator / Viewer: `None`

## Folded-in sub-decisions

1. **A credential owns a SET of named secret keys, not a 1:1 vault key.** A
   `CredentialEntity` addresses its secret material as `cred:{Id}:{slot}` (e.g.
   `cred:{Id}:password`, `cred:{Id}:token`, and for slice-5 `cred:{Id}:sshkey` +
   `cred:{Id}:passphrase`). A multi-component kind (SSH private key **plus** a
   passphrase) is then just two slots under one credential — **no schema change**
   when a kind needs a second secret component. The descriptor records which slots a
   kind populates; consume-time reads the slots the kind declares.

2. **The C1 promotion migration lands in this slice and runs as a startup task,
   after the KEK/DekProvider is live — not as a pure EF schema migration.** It must
   **decrypt to recompose**, so it cannot run inside the schema migration (which
   has no DEK). Sequence: (a) EF schema migration creates `CredentialEntity` +
   `Host.ManagementCredentialId`/`TransportCredentialId` and drops `UseGlobalCreds`;
   (b) a startup task, ordered **after** `DekProvider`/secret-store init in
   `Program.cs` (the `LoadPersistedCredsAsync` region, `Program.cs:962-980`, is the
   analogous existing post-vault startup step), reads legacy `global_winrm` +
   `host_cred:{hostId}` blobs, decrypts them, creates named `CredentialEntity` rows,
   writes secret material under the new `cred:{Id}:{slot}` keys, points each host's
   `ManagementCredentialId` at the right row, and removes the legacy keys.
   - **Proxmox token decomposition (preserve gotcha #13):** the legacy per-host
     Proxmox secret stores the **bare** `user@realm!tokenid=secret`. The promotion
     decomposes it into descriptor fields (`user@realm`, `tokenid`) held as non-secret
     `Descriptor` JSON and the **secret** UUID held in the vault slot — then the
     provider seam **recomposes** the bare token string at `ProxmoxProvider.BuildClient`
     exactly as today (still sent via `TryAddWithoutValidation`). Storage is
     structured; the wire format is unchanged.
   - The task must be **idempotent** (safe on a DB already migrated — no legacy keys
     left → no-op) and tolerant of the A2 half-state.

3. **Slice-5 SSH user (single `root` vs per-node) is an open build-time
   sub-question.** Whether the Proxmox SSH transport credential carries one `root`
   identity for the cluster or a per-node user is left to confirm against the
   `migrate-vm` skill when slice-5 is built. **The slot model holds either way:** a
   single SSH credential with `cred:{Id}:sshkey` (+ optional `cred:{Id}:passphrase`)
   referenced by one-or-many hosts' `TransportCredentialId` covers both shapes —
   this decision does not block on it.

## API surface (decided)

Credential CRUD returns **metadata only**; secret material is write-only and never
read back. Add-host / PATCH-host reference credentials by **id slot**, not raw secrets.

| Method | Path | Notes |
|---|---|---|
| GET | `/api/credentials` | List — **metadata only** (id, name, kind, platform, usedByHosts count). Projects from columns; **never** calls `ISecretStore.GetAsync`. Requires `ViewCredentials`. |
| GET | `/api/credentials/{id}` | Single — metadata only (incl. `usedByHosts` host list). No secret in the response. Requires `ViewCredentials`. |
| POST | `/api/credentials` | Create a named credential — secret in the body goes straight to the vault slot(s); response is metadata only. Requires `ManageCredentials` (Admin). |
| POST | `/api/credentials/{id}/rotate` | **Write-only** secret rotation — new secret → vault slot(s); response is metadata only, never the secret. Requires `ManageCredentials`. |
| DELETE | `/api/credentials/{id}` | **409 + `usedByHosts`** if referenced (B1); else deletes row + vault slot(s). Requires `ManageCredentials`. |
| POST | `/api/hosts` | Add host references `managementCredentialId` (+ optional `transportCredentialId`) instead of raw `username`/`password`/`token`. The old `useGlobalCreds`/`username`/`password`/`token` add-host fields (`AddHostsDto`, `Program.cs:1141`) are removed. |
| PATCH | `/api/hosts/{id}` | Repoint slots via `managementCredentialId`/`transportCredentialId`; the raw-secret PATCH fields (`PatchHostDto`, `Program.cs:1145`) are removed. |

The retired `POST /api/credentials` (global-WinRM setter, `Program.cs:387`) is
**replaced** by the CRUD surface above; the old route name is repurposed as the
create endpoint.

### Secret-never-leaves-server rule (invariant)

- **List/GET project from columns only.** Metadata endpoints **must not** call
  `ISecretStore.GetAsync`. A2 makes this structural — the metadata lives in the row.
- **Rotation is write-only.** Secret material is accepted on create/rotate and
  written to the vault; it is **never** returned in any response.
- **Decrypt only at the consume seam.** The only place a secret is read back is the
  provider building a client/transport (HV WinRM invoke, `ProxmoxProvider.BuildClient`,
  the slice-5 SSH executor) — server-side, never serialized to the client.

## Decision

**2026-06-19 · Operator (owner) approved the full model below.** Credentials become
first-class, named, reusable entities; global creds are retired; hosts reference
credentials via typed slots; the vault is Admin-managed.

- **Entity shape — Option C.** A flat `CredentialEntity` (TPH-style: `Id`, `Name`,
  `Kind`, `Platform` as real columns + a JSON `Descriptor` of **non-secret** fields)
  paired with a **closed `CredentialDescriptor` C# record union** as the typed
  parse/consume boundary. Secret material stays in `ISecretStore`. *(Rejected: A —
  loses exhaustiveness when slice-5's SSH kind lands; B — first persistence
  inheritance hierarchy, TPT read-cost/FK awkwardness.)*
- **Fork A — A2 (split storage).** Non-secret fields as columns / in `Descriptor`;
  only the secret in the vault. *(Rejected A1 — everything-in-blob forces decryption
  to read metadata.)*
- **Fork B — B1 (restrict delete while referenced).** 409 + `usedByHosts`.
  *(Rejected B2 — cascade/orphan reintroduces the silent-unreachable failure.)*
- **Fork C — C1 (retire global creds entirely).** Remove `POST /api/credentials`
  (old global setter), `Host.UseGlobalCreds`, and the `global_winrm` key; the global
  WinRM creds become a migrated named credential. *(Rejected C2 default-per-platform
  flag — available as a pure-UX fast-follow, no schema change.)*
- **Fork D — D2 (typed credential slots).** `Host.ManagementCredentialId` +
  `Host.TransportCredentialId` (transport null for HV; the Proxmox SSH key for
  slice-5 per ENG-0009). *(Rejected D1 — can't fit Proxmox's two secrets; D3 generic
  M:N — over-engineered.)*
- **RBAC — Admin-only vault.** Split `ManageCredentials` into manage (Admin:
  create/rotate/delete) + a new `ViewCredentials` flag (VmOperator: list-metadata-only
  to pick a credential when adding a host). `RbacCatalog` updated accordingly.

### Consequences (in effect)
- **Slots own a set of vault keys** (`cred:{Id}:{slot}`), so multi-component kinds
  (SSH key + passphrase) add a slot, not a schema change (sub-decision 1).
- **The C1 promotion runs in this slice as a startup task after the KEK/DekProvider**
  is live (not a pure EF migration), decrypting legacy `global_winrm` +
  `host_cred:{hostId}` blobs into named rows + new vault slots, idempotently;
  Proxmox tokens are decomposed for storage and **recomposed bare at the provider
  seam** (gotcha #13 preserved) (sub-decision 2).
- **Slice-5 SSH user (single `root` vs per-node) stays an open build-time
  sub-question** to confirm against the `migrate-vm` skill; the slot model holds
  either way and does not block this decision (sub-decision 3).
- **The credential-management design request is unblocked** — the engineering data
  model it assumed now exists (`design/requests/from-codebase/2026-06-19-credential-management.md`);
  the structured PVE-token entry (separate `user@realm` / `tokenid` / `secret`
  fields) maps directly onto the `ProxmoxToken` descriptor + its vault slot.
- **Documentation agent:** fold this into the persistence-and-security spec (new
  `CredentialEntity` + slot key convention + Admin-only vault) and the proxmox-integration
  spec (token decomposition/recomposition at the seam). Reference ENG-0012; do not
  re-litigate the forks here.

## Open sub-questions (build-time, do not reopen the decision)

- **Slice-5 SSH identity** — single cluster `root` key vs per-node user; confirm
  against the `migrate-vm` skill when slice-5 lands (sub-decision 3). The slot model
  is agnostic.
- **Create/delete cross-store ordering** — exact write/rollback order across the row
  and the vault slot(s) under A2 (write vault → row on create; row → vault on delete),
  and how the startup promotion tolerates a half-written legacy state. Spec/build detail.
