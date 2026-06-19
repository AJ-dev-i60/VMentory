# Persistence & Security

> Component spec · expands [ARCHITECTURE.md §3 Persistence](../ARCHITECTURE.md#3-persistence-hybrid--eng-0002)
> and [§5 Security](../ARCHITECTURE.md#5-security-this-is-now-a-hosted-service-not-loopback).
> **Decided:** secrets via an **`ISecretStore`** abstraction with v1 **app-native envelope
> encryption** + a **runtime-injected KEK**, **tokens-over-passwords** as a binding principle
> (ENG-0002); the agent PKI is a **private CA in Core** (root+intermediate, short-lived certs +
> auto-renew, revocation = stop-renew + registry allow/deny, **no CRL/OCSP**) (ENG-0005);
> **single-operator — no `tenant_id`** (ENG-0006). This spec is the propagation target for
> ENG-0002/0005.

Phase 1 is ephemeral by design: in-memory `Store` ([Store.cs:5](../../../Store.cs#L5)), purge on
quit ([Program.cs:489](../../../Program.cs#L489), [Program.cs:502](../../../Program.cs#L502)),
errors-only log with no PII ([Program.cs:561](../../../Program.cs#L561) `ErrorLogger`). Phase 2 is
a hosted, multi-platform, **read/write** service — it must remember hosts, history, and jobs, and
it must do real auth. This spec defines exactly what is persisted, what is not, and how secrets and
access are handled.

---

## 1. Schema sketch

Datastore: **SQLite by default** (single file in a mounted volume), **Postgres opt-in** for
multi-instance, via **EF Core** ([ARCHITECTURE.md §3](../ARCHITECTURE.md#3-persistence-hybrid--eng-0002)).
Tables (sketch — column lists are indicative, not final):

**Single-operator (ENG-0006): no `tenant_id` on any table** — no per-tenant isolation or row-level
scoping. Roles for one org only.

```
provider_registration
  id, platform (HyperV|Proxmox), display_name,
  endpoint (agent address | PVE node/cluster URL),
  secret_ref         -- POINTER into ISecretStore, NOT a secret
  capabilities_json  -- cached ProviderCapabilities
  created_at, updated_at

host                      -- inventory identity (was Models.cs Host, generalized)
  id, provider_id -> provider_registration,
  native_id, fqdn, os_caption, cpu_model, total_cores, total_ram_gb, ...
  management_credential_id -> credential   -- D2 slot (ENG-0012): control plane (HV WinRM / PVE token)
  transport_credential_id  -> credential   -- D2 slot (ENG-0012): null for HV; PVE SSH key (slice 5)
  -- NOTE (ENG-0012): the Phase-1 `use_global_creds` bool is REMOVED — see §4a.

credential                -- named, reusable credential (ENG-0012, flat TPH; planned, not yet built)
  id, name, kind (HyperVPassword|ProxmoxToken|ProxmoxSshKey), platform (HyperV|Proxmox),
  descriptor_json    -- NON-SECRET structured fields only (e.g. PVE user@realm + tokenid, SSH username)
  created_at, rotated_at
  -- Secret MATERIAL is NOT here: it lives in ISecretStore under cred:{id}:{slot} keys (§4a).

inventory_snapshot        -- the historical record Phase 1 never kept
  id, host_id -> host, taken_at,
  payload_json            -- HostInfo + VmInfo[] at that instant

vm_record                 -- optional normalized current-state (or derive from latest snapshot)
  id, host_id, native_id, name, state, vcpu, ram_mb, ...

operation_job             -- generalized from migration_job (ENG-0006): migrate|deploy|backup|restore
  id, kind, source_ref, target_provider_id, mode (dry-run|run),
  status, created_by, created_at, updated_at

operation_step
  id, job_id -> operation_job, ordinal, name, depends_on,
  status (Pending|Running|Succeeded|Failed|Skipped|RolledBack),
  inputs_json, outputs_json, idempotency_key, started_at, finished_at

operation_step_log
  id, step_id -> operation_step, ts, level, message   -- structured, persisted

secret_metadata           -- ISecretStore metadata; the VALUE is the encrypted blob, never here in clear
  id, secret_ref, scope (provider/host/ca), kind (pve_token|ssh_key|smb_cred|agent_cert|ca_key),
  created_at, rotated_at, last_used_at

agent_identity            -- PKI allow/deny + enrolled agents (ENG-0005)
  id, host_id, cert_fingerprint, status (allowed|denied|pending),
  enrolled_at, cert_not_after, renewed_at      -- revocation = set denied / stop renewing

audit_event
  id, ts, actor (user|agent), action, target_ref, result, detail_json

app_user                  -- local accounts now; OIDC seam later (ENG-0008, §5b)
  id, username, password_hash (PBKDF2-SHA256, BCL-only, work-factor in hash), role,
  must_change_password, created_at, last_login_at

role_permission           -- the pillar×verb catalog: role -> permission mapping (ENG-0008, §5c)
  role, permission          -- permission = ProviderCapability verb | console-only permission
  -- NOTE: v1 implementation uses static code (RbacCatalog.cs) rather than a DB table.
  -- A custom-roles DB table is the non-breaking later addition path.
```

> The encrypted secret blobs live in their own table managed by the app-native `ISecretStore` impl
> (§4); `secret_metadata` carries the scope/timestamps the registry references by `secret_ref`.

**Inventory snapshots are the key Phase-1→2 upgrade.** Phase 1 computes a *session* diff in
memory ([Store.cs:75](../../../Store.cs#L75) `RecordDiff`, keyed `hostId:name`
[Store.cs:82](../../../Store.cs#L82)) and loses it on quit. Persisting `inventory_snapshot` turns
that into **real historical diff** ([ARCHITECTURE.md §3](../ARCHITECTURE.md#3-persistence-hybrid--eng-0002),
[ROADMAP.md §2.1](../ROADMAP.md) historical stats). **Keep the diff algorithm** from
[Store.cs:75](../../../Store.cs#L75) — it's correct — but feed it two persisted snapshots instead
of previous-vs-current in-memory lists, and resolve the identity caveat in
[provider-abstraction.md Open question 1](provider-abstraction.md#7-open-questions-need-a-human-decision)
before relying on cross-session diffs.

---

> **Built (slices 3 / (2) / (3), 2.0 persistence).** Three EF migrations are in
> `VMentory.Core/Migrations`:
> - **`Initial`** (slice 3): `host` (as `HostRegistrationEntity`) + `inventory_snapshot`
>   (`InventorySnapshotEntity`). Diff is snapshot-fed (latest two snapshots, ordered by the autoincrement
>   `id` — SQLite can't `ORDER BY` a `DateTimeOffset`).
> - **`AddAuth`** (re-baselined slice (2)): `audit_event` (`AuditEventEntity`) + `app_user`
>   (`AppUserEntity` — `id, username, password_hash (PBKDF2-SHA256), role, must_change_password,
>   created_at, last_login_at`). `role_permission` is static code (`RbacCatalog.cs`) — not a DB table.
> - **`AddSecrets`** (re-baselined slice (3)): `SecretEntity` (encrypted blob + nonce) +
>   `DekEntity` (wrapped DEK). `AesGcmSecretStore` encrypts credentials in SQLite; `VMENTORY_KEK` gates
>   persistence; restored hosts load creds from `ISecretStore` at startup.
> **Not yet built:** `provider_registration`, `vm_record`, `operation_*`, `agent_identity` — they arrive
> with their owning slices (Proxmox provider, operations engine, HV agent/PKI). The **`credential`
> table + `host.management_credential_id`/`transport_credential_id` slots + the C1 promotion
> startup task** (ENG-0012, §4a) are **planned for the credential-management slice** (slice-5 era,
> ahead of the Proxmox SSH key) — not yet built.

## 2. What is persisted

- Provider/host **registry** (addresses, display names, cached capabilities, **secret refs**).
- **Inventory snapshots** → historical view + diff.
- **Operation jobs, steps, step logs** — migrate / deploy / backup / restore
  ([migration-job-model.md §1](migration-job-model.md#1-why-a-persisted-dag), generalized per
  ENG-0006) — must survive restart for resume.
- **Secret metadata** + the encrypted secret blobs via `ISecretStore` (§4). Values are encrypted,
  never plaintext (ENG-0002).
- **Agent PKI state** — enrolled agent identities + the **allow/deny list** (ENG-0005); revocation
  is a state change here, not a CRL.
- **Audit trail** (§6).
- Local **user accounts** (hashed) if not using external IdP (§5).

## 3. What is NOT persisted (in the DB)

- **Credentials, API tokens, SSH keys.** Never in plaintext at rest — Decision 2. The DB stores a
  `secret_ref` pointer only; the secret itself lives in the secret store (§4). This is the hard
  line the ARCHITECTURE draws ([ARCHITECTURE.md §3](../ARCHITECTURE.md#3-persistence-hybrid--eng-0002)).
- No host/VM **PII in logs** beyond what audit explicitly records — continues the Phase-1
  `ErrorLogger` discipline of "errors only, no host/PII data"
  ([Program.cs:8](../../../Program.cs#L8), [Program.cs:561](../../../Program.cs#L561)).

---

## 4. Secret handling — `ISecretStore` + envelope encryption (decided + BUILT, ENG-0002)

> **Built (re-baselined slice (3), 2026-06-18).** `VMentory.Core/Secrets/`: `ISecretStore` interface,
> `AesGcmSecretStore` (DB-backed, scoped, AES-256-GCM), `EphemeralSecretStore` (singleton, in-memory
> fallback), `DekProvider` (KEK/DEK manager). `SecretEntity`/`DekEntity` + `AddSecrets` migration.
> `VMENTORY_KEK` env var (base64, 32 bytes) gates persistence. Global WinRM creds + per-host creds
> persist and are restored at startup. The `rotate` operation is **not yet implemented** — set+delete
> is the current API. **ENG-0012 (planned)** layers named, reusable credentials on top of this same
> `ISecretStore` substrate (the slot keys `cred:{Id}:{slot}` replace the ad-hoc `global_winrm` /
> `host_cred:{hostId}` keys) and adds the write-only `rotate` endpoint at the credential layer — see
> [§4a](#4a-named-credentials--first-class-reusable-entities-decided-eng-0012-planned).

**`ISecretStore` is a provider abstraction (ENG-0002), mirroring `IVirtualizationProvider`.** Call
sites depend on the interface, never on the mechanism, so the store evolves without code churn.

- **Interface:** `get` / `set` / `delete` — **`rotate` is planned but not yet in the interface** (the
  v1 `ISecretStore` has `SetAsync`/`GetAsync`/`DeleteAsync` only).
- **v1 default impl = app-native envelope encryption.** Values encrypted with **AES-256-GCM**
  (.NET `AesGcm` / libsodium) and stored in SQLite; a per-DB **Data Encryption Key (DEK)** encrypts
  values; a **Key Encryption Key (KEK)** wraps the DEK. **Only the KEK comes from outside.** No
  second service to run; no plaintext at rest.
- **KEK source = runtime-injected** by default (Docker/Podman secret, systemd `LoadCredential`, or
  env var) for unattended operation. **Operator-passphrase is an opt-in mode** for high-security
  deployments; hardware-rooting (TPM/KMS) is a future upgrade behind the same interface.
- **Vault/OpenBao + Azure Key Vault are optional providers behind the same interface, later** —
  **not v1**.
- **In memory:** secrets are decrypted into memory only, held for the operation, and zeroed. The
  Phase-1 discipline **carries forward verbatim** — `Credentials` holds the secret as `byte[]` and
  `Array.Clear`s it on dispose ([Models.cs:121](../../../VMentory.Core/Models.cs#L121),
  [Models.cs:143](../../../VMentory.Core/Models.cs#L143)); `Store` disposes creds on removal and purge
  ([Store.cs:41](../../../Store.cs#L41), [Store.cs:133](../../../Store.cs#L133)). Reuse this for PVE
  tokens, SSH keys, SMB creds, and CA key material.

**Binding principle — prefer scoped keys/tokens over passwords everywhere (ENG-0002).** The best
secret is one we don't store; the second-best is scoped and revocable without a human password
reset. Concretely:

| Secret | Form (tokens-over-passwords) |
|---|---|
| Hyper-V control | **agent mTLS client cert** — *not* a Windows domain password (eliminated in ENG-0001) |
| Proxmox API | **privilege-separated API token** — *not* a root ticket ([proxmox-integration.md §1](proxmox-integration.md#1-authentication)) |
| Proxmox node shell | **dedicated SSH key**, ideally forced-command — *not* an SSH password |
| VHDX share (migration) | SMB/CIFS cred only where unavoidable, injected at job runtime — replaces the skill's plaintext `/root/.smbcreds` |
| Agent enrollment | single-use, short-lived **enrollment token** ([agent-protocol.md §3](agent-protocol.md#3-authentication-trust--enrollment-decided-eng-00030005)) |

### CA key custody (ENG-0005)

The private CA's key (root + intermediate, §5) is the **highest-value secret** — its compromise =
fleet compromise. It lives in **`ISecretStore`**, KEK-wrapped, and is the natural case for the
opt-in **operator-passphrase KEK mode**. Open sub-question (ENG-0002): KEK rotation re-wraps the DEK
only, without decrypting every secret to plaintext.

---

## 5. Runtime, deployment & auth model

> **Decided:** Core is a **containerized, web-first, install-nothing hosted service** (ENG-0010) and
> console access is **login + fixed-role RBAC** (ENG-0008) — both Decided 2026-06-16. The Phase-1
> loopback + single-session-token model is **fully replaced**, not extended.

### 5a. Runtime & deployment contract (decided, ENG-0010)

Phase-1 security — random free port, a 24-byte session token, **127.0.0.1 only**
([Program.cs:37](../../../Program.cs#L37) `UseUrls("http://127.0.0.1:…")`,
[Program.cs:84](../../../Program.cs#L84) token middleware, [Program.cs:528](../../../Program.cs#L528)
`GenerateToken`) — is a **loopback desktop app** and **does not survive** becoming a shared hosted
service. It assumes a single local operator on loopback; Phase 2 is a container reachable over the
network by multiple users. ENG-0010 fixes the runtime contract for release 1:

- **Packaging:** single Linux container image (ASP.NET Core 8 app + an SSH client for the Proxmox
  residue channel, [proxmox-integration.md §1](proxmox-integration.md#1-authentication)); **no
  `qemu`/`qm` baked in** — those run on the PVE node (ENG-0009). The only install action is
  `docker run` / compose; the user installs nothing else locally.
- **Network bind:** bind **`0.0.0.0:{configurable port}`** (env-driven, e.g. `VMENTORY_HTTP_ADDR`),
  replacing the hardcoded `127.0.0.1:{random}` and dropping `FindFreePort()`
  ([Program.cs:37](../../../Program.cs#L37)).
- **TLS termination (decided default):** **Core-terminated HTTPS — Kestrel serves HTTPS directly**
  with an operator-provided or **self-signed-with-warning** cert (first-run fallback; not
  refuse-to-start). A **reverse-proxy seam** (nginx/Traefik terminating TLS) is a **documented
  alternative, NOT required for release 1**. This dashboard TLS is a **distinct trust domain** from
  the agent mTLS PKI (§5d / ENG-0005) — do not conflate the dashboard cert with the internal CA.
- **Runtime KEK / cert injection:** the envelope-encryption **KEK is injected at runtime** (ENG-0002,
  §4); the **dashboard TLS cert/key are likewise injected at runtime** (mounted volume or env). The
  image bakes in **no secret** of any kind.
- **Persistence volume:** the SQLite DB lives on a **mounted volume** via `VMENTORY_DB` (already
  wired; CLAUDE.md gotcha 7); TLS material + operator config mount alongside. Postgres stays the
  opt-in multi-instance path (§1).
- **No inbound from PVE nodes:** Core initiates all Proxmox connections outbound (REST 8006 + SSH 22,
  ENG-0009); nodes never connect *in*. The **first containerized releases expose only the web UI
  port**. The agent gRPC/mTLS listener is **HV-only and arrives later with the migration slice**
  (ENG-0009 / §5d), not in the first releases.

### 5b. Console authn — login replaces the session token (decided + BUILT, ENG-0010 + ENG-0008)

> **Built (re-baselined slice (2), 2026-06-18).** The session token is retired; `POST /api/auth/login`
> issues an HttpOnly `vmentory_session` cookie (Secure, SameSite=Strict, 12h sliding). SSE auth rides
> the cookie; no `?token=` query param. The authz chokepoint middleware is live in `Program.cs`.

- **Minimal admin login** landed **in re-baselined slice (2)**, replacing the single session token and
  the `/api/quit` desktop affordance. **Built in re-baselined slice (2).**
  Local account passwords are hashed with **PBKDF2-SHA256** (BCL-only, 100k iterations, work-factor
  stored in the hash; `VMentory.Core/Auth/PasswordHasher.cs`), never the Phase-1 single shared token.
  *(The schema sketch above used Argon2id; the implementation uses PBKDF2-SHA256 for BCL-only
  portability — no external NuGet required.)*
- **Identity:** **local accounts now** (hashed credential, KEK-wrapped per ENG-0002), with an
  **OIDC/SSO seam later** (a future identity provider behind the same authz chokepoint, §5c).
- **Sessions:** issue a real session cookie / bearer after login. The Phase-1 token-in-query-param
  pattern ([Program.cs:88](../../../Program.cs#L88), and `?token=` for SSE
  [Program.cs:414](../../../Program.cs#L414)) leaks tokens into logs/history and **is removed**; SSE
  auth rides the authenticated session, not a query param.

### 5c. Console authz — fixed-role RBAC (decided + BUILT, ENG-0008)

> **Built (re-baselined slice (2), 2026-06-18).** `RbacCatalog.cs` (static role→capability mapping),
> `AppRole` enum (Viewer/VmOperator/BackupOperator/Admin), `ConsolePermission` flags enum. Single
> authz chokepoint middleware in `Program.cs`. `AddAuth` migration added `app_user` + `audit_event`.

Console RBAC is **Decided (ENG-0008, 2026-06-16) and built**: fixed built-in roles over a pillar×verb catalog,
firm + early — no longer "roles later / undecided."

- **Roles:** **fixed built-in roles** — **Admin / VM-operator / Backup-operator / Viewer** — backed
  by an **internal permission catalog keyed on pillar×verb** (role→permission mapping is *data*, not
  hardcoded `if`s). **No custom-role authoring in release 1**; a custom-roles UI is a non-breaking
  later addition behind the same enforcement. The `app_user.role` column (§1) carries the assigned
  role; a `role_permission` mapping (catalog) is the seam for later custom roles.
- **Permission taxonomy:** **reuse `ProviderCapability`**
  ([VMentory.Core/ProviderCapability.cs](../../../VMentory.Core/ProviderCapability.cs)) as the
  provider-action axis (Start/Stop/Reconfigure/Provision/Backup/…), **plus a small console-only
  permission set** for non-provider actions (`manage-credentials`, `manage-enrollment`, `view-audit`,
  `manage-users`). One catalog, two sources.
- **Enforcement — single audited chokepoint:** a **single authorization chokepoint** in
  `VMentory.Web`/API **and** the operations engine, so a job dispatched by the API and a job resumed
  by the engine honor the **same** check. This chokepoint **must exist before any write verb is
  exposed**, and **every allow/deny is audited** (§6). This console RBAC is **distinct from** the
  agent's constrained-verb authz (ENG-0004): RBAC decides *which operator* may invoke *which
  pillar/verb*; the agent independently fixes *what verbs exist at all*.
- **Scoping:** **org-wide only in the first cut** (single-operator tenancy, no `tenant_id` — ENG-0006);
  per-host / host-group scoping is a later catalog extension.
- **Sequencing (firm + early):** minimal admin login lands in the ENG-0010 deployment slice; **the
  fixed roles + pillar×verb catalog + authz chokepoint land *before any write verbs are exposed*** —
  i.e. with/just-before Proxmox management/Deploy, **not** deferred to a later "auth hardening" pass.
  Backup-operator simply has no Backup verbs to grant until pillar 4 ships, but the framework exists
  from the first write-capable release.

### 5d. Channel auth & transport

- **Core ↔ Agent (Hyper-V only):** **mutual mTLS** (decided, ENG-0004 — not "or signed tokens"); the
  agent accepts only Core's identity
  ([agent-protocol.md §3](agent-protocol.md#3-authentication-trust--enrollment-decided-eng-00030005)).
  Per ENG-0009 this channel and its PKI (§5e) are **scoped to the Hyper-V migration source** and
  **arrive with the migration slice**, not the first containerized releases.
- **Core → Proxmox (no agent):** scoped, privilege-separated **API token** (REST) + a constrained,
  forced-command **SSH key** (residue channel), both from `ISecretStore` and revocable (ENG-0009,
  [proxmox-integration.md §1](proxmox-integration.md#1-authentication)). Core always initiates;
  nodes never connect in (§5a).
- **Provider secrets:** from `ISecretStore`, scoped (§4).
- **Transport:** the browser↔Core hop is **Core-terminated HTTPS** (§5a), not the Phase-1
  plain-HTTP-on-loopback.

### 5e. Agent PKI — private CA in Core (decided, ENG-0005; HV-scoped, deferred to migration slice)

The mTLS that secures Core↔Agent is backed by a **private CA inside Core** (external-CA / AD CS seam
deferred). **Scope (ENG-0009 amendment, 2026-06-16):** this CA serves **only the Hyper-V agent
fleet** (~5 retiring hosts) — **not** "every node on both platforms"; Proxmox uses no agent and no
internal CA (its node trust is the still-open SSH/TLS question in
[proxmox-integration.md §5](proxmox-integration.md#5-open-questions-need-a-human-decision)). The PKI
design below is unchanged and still correct, but it is **deferred off the 2.0 foundation to the
agent/migration slice** (its only consumer). It is a **separate trust domain from the
operator-facing dashboard TLS** (§5a / ENG-0010) — the internal CA does **not** terminate the web
UI's HTTPS.

- **Structure:** an offline-ish **root** signs a single **intermediate**; the intermediate does
  day-to-day signing. **Agents pin the root**; Core signs agent + server certs with the
  **intermediate**, so the signing key rotates **without a fleet-wide re-pin**.
- **Lifetime:** agent certs are **short-lived** (days/weeks) and **auto-renew over the existing mTLS
  channel** before expiry.
- **Revocation = stop renewing + the `agent_identity` allow/deny list** (§1). **No CRL/OCSP** to run
  or distribute — air-gap-friendly. An identity set to `denied` (or dropped from `allowed`) fails
  the next handshake.
- **CA key custody:** in `ISecretStore`, KEK-wrapped (§4).
- **Enrollment:** single-use token → CSR (key never leaves the host) → intermediate-signed client
  cert + root to pin ([agent-protocol.md §3](agent-protocol.md#3-authentication-trust--enrollment-decided-eng-00030005)).
- Core gains a small **internal-CA component** (issue / sign / renew / list / deny) backed by
  `ISecretStore` — buildable in 2.0 alongside enrollment.

---

## 4a. Named credentials — first-class reusable entities (decided, ENG-0012; planned)

> **Decided (ENG-0012, 2026-06-19) — planned for the credential-management slice (slice-5 era), not
> yet built.** Credentials become **first-class, named, reusable** entities a host *references* via
> typed slots, instead of the Phase-1 ad-hoc per-host blobs + the magic global-WinRM credential. This
> section supersedes the Phase-1 credential storage model described inline elsewhere; where another
> section still describes "global WinRM creds + per-host creds," treat it as **superseded-by-ENG-0012**.

### Storage model — flat `CredentialEntity` (TPH) + closed C# descriptor union (Option C, A2)

A credential is **split** between the DB and `ISecretStore` (Fork A2):

- **Non-secret metadata** is a real `credential` row (§1): `Id`, `Name`, `Kind`, `Platform` as
  columns + a JSON `Descriptor` of **non-secret structured fields**. One flat TPH-style table —
  matches the rest of the persistence layer; **no inheritance hierarchy**.
- **Secret material** stays in `ISecretStore` — **never** in the `credential` row or its `Descriptor`.
- A **closed `CredentialDescriptor` C# record union** in `VMentory.Core` is the typed parse/consume
  boundary over the JSON column. Members: `HyperVPassword`, `ProxmoxToken`, `ProxmoxSshKey`. A `switch`
  over the closed union is **exhaustive** — adding the slice-5 SSH kind is a compile error everywhere
  it must be handled (the reason Option C beat the loose discriminator Option A).

Why A2: list / GET / "used by N hosts" all **project straight from columns** — no decryption to render
metadata. The secret-never-leaves-server rule (below) becomes *structural*: the metadata path has no
vault call. The cost accepted is **two stores to keep consistent** (row + vault key[s]); create writes
vault-then-row, delete writes row-then-vault, and the C1 promotion + delete paths must tolerate a
half-written state. (Exact cross-store ordering is a flagged build-time detail in ENG-0012.)

### A credential owns a SET of named secret keys — `cred:{Id}:{slot}`

A credential addresses its secret material as a **set** of slot keys, not a 1:1 vault key
(sub-decision 1):

| Kind | Vault slots | Descriptor (non-secret) |
|---|---|---|
| `HyperVPassword` | `cred:{Id}:password` | username |
| `ProxmoxToken` | `cred:{Id}:token` (the secret UUID) | `user@realm`, `tokenid` |
| `ProxmoxSshKey` (slice 5) | `cred:{Id}:sshkey` (+ optional `cred:{Id}:passphrase`) | SSH username |

A multi-component kind (SSH private key **plus** a passphrase) is then just two slots under one
credential — **no schema change** when a kind needs a second secret component. The descriptor records
which slots a kind populates; consume-time reads the slots the kind declares.

### How a host references credentials — typed slots (D2)

`Host` carries **two nullable credential-slot FKs** (§1), not a raw secret and not the Phase-1
`UseGlobalCreds` bool:

- `ManagementCredentialId` — the control-plane credential: **HV WinRM**, or the **PVE API token**.
- `TransportCredentialId` — the data/disk-op credential: **null for Hyper-V**; the **Proxmox SSH key**
  for the slice-5 disk-import / guest-edit residue ([proxmox-integration.md §3](proxmox-integration.md#3-rest-vs-ssh--the-boundary), ENG-0009).

The provider seam reads exactly the slot it needs. HV simply leaves `TransportCredentialId` null. A
host needing a third role in some future platform is a third named slot, not an M:N join table.

### Deleting a referenced credential — restrict (B1)

`DELETE /api/credentials/{id}` returns **409 Conflict** with a `usedByHosts` list when any `Host` slot
references it; delete succeeds only at zero references. No host is ever silently left with a dangling
slot (rejected B2 cascade/orphan, which reintroduces the silent-unreachable failure ENG-0012 exists to
kill). Mirrors the Phase-1 host-delete-clears-its-secret referential safety.

### RBAC — Admin-only vault (split flag)

The single Phase-1 `ConsolePermission.ManageCredentials` (granted to Admin **and** VmOperator) is
**split** so the vault — now holding the keys to every host — is Admin-managed:

- `ConsolePermission.ManageCredentials` → **Admin only** (create / rotate / delete).
- **new** `ConsolePermission.ViewCredentials` → **Admin + VmOperator** (list **metadata only** —
  name / kind / platform / used-by — to pick a credential when adding a host; cannot create, rotate,
  reveal, or delete).

Resulting `RbacCatalog` console mapping (see [§5c](#5c-console-authz--fixed-role-rbac-decided--built-eng-0008)):
Admin = `ViewAudit | ManageCredentials | ViewCredentials | ManageEnrollment | ManageUsers`;
VmOperator = `ViewCredentials` only; BackupOperator / Viewer = `None`.

### Secret-never-leaves-server rule (invariant)

- **List / GET project from columns only** — metadata endpoints **must not** call
  `ISecretStore.GetAsync`. A2 makes this structural.
- **Rotation is write-only** — secret material is accepted on create / rotate and written to the vault;
  it is **never** returned in any response.
- **Decrypt only at the consume seam** — the only read-back is server-side, building a client /
  transport (HV WinRM invoke, `ProxmoxProvider.BuildClient`, the slice-5 SSH executor); never serialized
  to the client.

### C1 — global credentials retired + the promotion migration

The Phase-1 model is **fully retired** (C1): `POST /api/credentials` (the old global-WinRM setter),
`Host.UseGlobalCreds`, and the `global_winrm` vault key are **removed**. The "define once, reuse" goal
is met by named credentials directly — global was just an un-named, un-rotatable special case.

The promotion runs as a **startup task, AFTER the KEK/`DekProvider` is live** — *not* a pure EF schema
migration, because it must **decrypt to recompose** (the schema migration has no DEK). Sequence:

1. **EF schema migration:** create `credential` + `host.management_credential_id` /
   `transport_credential_id`; drop `use_global_creds`.
2. **Startup task** (ordered after secret-store init — the analogue of the existing
   `LoadPersistedCredsAsync` post-vault step): read legacy `global_winrm` + `host_cred:{hostId}` blobs,
   **decrypt** them, create named `credential` rows, write secret material under the new
   `cred:{Id}:{slot}` keys, point each host's `ManagementCredentialId` at the right row, remove the
   legacy keys. **Idempotent** (no legacy keys → no-op) and tolerant of the A2 half-state.

**Proxmox token decomposition (preserves CLAUDE.md gotcha #13):** the legacy per-host Proxmox secret
holds the **bare** `user@realm!tokenid=secret`. The promotion **decomposes** it into non-secret
`Descriptor` fields (`user@realm`, `tokenid`) + the **secret** UUID in the vault slot — and the
provider seam **recomposes** the bare token at `ProxmoxProvider.BuildClient` exactly as today (still
sent via `TryAddWithoutValidation`). Storage is structured; the wire format is unchanged. See
[proxmox-integration.md §1](proxmox-integration.md#1-authentication).

### Credential CRUD API surface (metadata-only responses)

| Method | Path | Notes | Permission |
|---|---|---|---|
| GET | `/api/credentials` | List — **metadata only** (id, name, kind, platform, usedByHosts count). Projects from columns; **never** `ISecretStore.GetAsync`. | `ViewCredentials` |
| GET | `/api/credentials/{id}` | Single — metadata only (incl. `usedByHosts` list). No secret. | `ViewCredentials` |
| POST | `/api/credentials` | Create — secret in body → vault slot(s); response metadata only. (Repurposes the retired global-WinRM route name.) | `ManageCredentials` |
| POST | `/api/credentials/{id}/rotate` | **Write-only** rotation — new secret → vault slot(s); never returns the secret. | `ManageCredentials` |
| DELETE | `/api/credentials/{id}` | **409 + `usedByHosts`** if referenced (B1); else delete row + vault slot(s). | `ManageCredentials` |

`POST /api/hosts` / `PATCH /api/hosts/{id}` reference credentials by **id slot**
(`managementCredentialId` (+ optional `transportCredentialId`)) instead of raw
`username`/`password`/`token`/`useGlobalCreds`; those raw-secret add-host/PATCH fields are **removed**.

### Open build-time sub-question (does not reopen the decision)

- **Slice-5 SSH identity** — single cluster `root` key vs per-node user. The slot model holds either
  way (one `ProxmoxSshKey` credential referenced by one-or-many hosts' `TransportCredentialId`), so this
  does not block. Confirm against the `migrate-vm` skill when slice-5 lands. Tracks alongside
  [proxmox-integration.md §5 OQ4](proxmox-integration.md#5-open-questions-need-a-human-decision).

---

## 6. Audit log

Every **write / management / migration / deploy / backup** action is recorded
([ARCHITECTURE.md §5](../ARCHITECTURE.md#5-security-this-is-now-a-hosted-service-not-loopback),
[ROADMAP.md §2.2](../ROADMAP.md) + [cross-cutting](../ROADMAP.md#cross-cutting-every-milestone)).
Schema: `audit_event` (§1) — `ts, actor, action, target_ref, result, detail_json`. **`ISecretStore`
emits an audit event per access** (ENG-0002), and the **agent logs every executed verb back to
Core** (ENG-0004) — `actor` may be a user or an agent. Read-only inventory scans need not be
audited; lifecycle ops, credential/secret access, provisioning, and every operation step do. Audit
rows are **append-only** and must **never** contain secret values.

---

## 7. Open / resolved questions

**Resolved (do not re-open):**
- ~~Multi-tenant vs single-operator~~ → **single-operator, no `tenant_id`** (ENG-0006).
- ~~Which secret store for v1~~ → **`ISecretStore` + app-native envelope encryption, runtime-injected
  KEK** (ENG-0002); Vault/Azure KV are optional later providers.
- ~~mTLS PKI ownership~~ → **private CA in Core**, root+intermediate, short-lived + auto-renew,
  allow/deny revocation, no CRL/OCSP (ENG-0005); **HV-scoped, deferred to the migration slice**
  (ENG-0009 amendment).
- ~~Core runtime/deployment model (bind, TLS, KEK injection, volumes)~~ → **containerized Core,
  `0.0.0.0:{configurable}`, Core-terminated HTTPS (reverse-proxy a documented seam), runtime
  KEK/TLS injection, SQLite on a mounted volume** (ENG-0010, §5a).
- ~~Loopback + session-token auth~~ → **replaced by login + RBAC** (ENG-0010 + ENG-0008, §5b–§5c).
- ~~Console RBAC / scoped roles~~ → **fixed roles (Admin / VM-operator / Backup-operator / Viewer)
  over a pillar×verb catalog reusing `ProviderCapability` + a small console-permission set; single
  audited authz chokepoint before any write verb; firm + early** (ENG-0008, §5c).
- ~~Credential storage model (global creds vs per-host vs named entities)~~ → **first-class named
  `CredentialEntity` (flat TPH + JSON `Descriptor` + closed C# union; secrets stay in `ISecretStore`
  under `cred:{Id}:{slot}` slots); typed `Host` slots (`ManagementCredentialId`/`TransportCredentialId`);
  global creds retired (C1, promoted to a named cred); split storage (A2); delete restricted while
  referenced (B1, 409 + usedByHosts); Admin-only vault (split `ManageCredentials` + new
  `ViewCredentials`); metadata-only CRUD, write-only rotation** (ENG-0012, §4a). *Slice-5 SSH identity
  (single root vs per-node) stays an open build-time sub-question; the slot model is agnostic.*

**Still open (need a human decision):**
1. **Snapshot retention / cadence.** How often to snapshot inventory and how long to retain
   (storage vs history depth)? Affects DB growth, especially on SQLite. Needs an operator-facing
   policy.
2. **At-rest encryption of the DB itself.** The DB holds no plaintext secrets (§3), but inventory
   and audit data may be sensitive. Encrypt the SQLite file / require encrypted Postgres, or treat
   the host volume as the trust boundary? **Owner decision.**
3. **KEK rotation / re-wrap procedure** (ENG-0002 sub-question) — rotate the KEK without decrypting
   every secret to plaintext (re-wrap the DEK only). Spec detail.
4. **Storage / repository layer persistence** (ENG-0006) — when Deploy/Backup land, the ISO/image/
   backup repository and its metadata need a home; placement is an open ENG topic. Note the
   dependency; do not schema it yet.
5. **PVE node SSH/TLS trust model** ([proxmox-integration.md §5](proxmox-integration.md#5-open-questions-need-a-human-decision)) —
   how the Core container trusts each PVE node's API cert and SSH host key (pin-on-onboard vs
   supplied fingerprint). Distinct from the agent PKI (§5e) and the dashboard TLS (§5a). **Owner
   decision; verify against a live node.**
