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
  id, username, password_hash (Argon2id), role, created_at

role_permission           -- the pillar×verb catalog: role -> permission mapping (ENG-0008, §5c)
  role, permission          -- permission = ProviderCapability verb | console-only permission
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

> **Built so far (slice 3, 2.0 persistence).** Only two of the sketched tables exist today:
> `host` (as `HostRegistrationEntity` — `id, platform, address, use_global_creds, added_at`) and
> `inventory_snapshot` (`id, host_id, taken_at, payload_json`) in `VMentory.Core/Persistence`
> (EF Core + SQLite, `Initial` migration). The diff is **already snapshot-fed** (latest two snapshots,
> ordered by the autoincrement `id` — SQLite can't `ORDER BY` a `DateTimeOffset`). **Not yet built:**
> `provider_registration`, `vm_record`, `operation_*`, `secret_metadata`, `agent_identity`,
> `audit_event`, `app_user`, `role_permission` — they arrive with their owning slices (`ISecretStore`,
> the operations engine, the containerized-Core/login+RBAC slice, the HV agent/PKI). **No credentials
> persist yet** — there is no `secret_ref` wiring until `ISecretStore` lands; restored hosts have null
> creds.

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

## 4. Secret handling — `ISecretStore` + envelope encryption (decided, ENG-0002)

**`ISecretStore` is a provider abstraction (ENG-0002), mirroring `IVirtualizationProvider`.** Call
sites depend on the interface, never on the mechanism, so the store evolves without code churn.

- **Interface:** `get` / `set` / `rotate` / `delete` — **rotation is first-class**, not bolted on —
  and **emits an audit event per access** to the history store (§6).
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

### 5b. Console authn — login replaces the session token (decided, ENG-0010 + ENG-0008)

- **Minimal admin login** lands **in the containerized-Core deployment slice** (ENG-0010), replacing
  the single session token and the `/api/quit` desktop affordance. Local account passwords are hashed
  with **Argon2id** (the `app_user` table, §1), never the Phase-1 single shared token.
- **Identity:** **local accounts now** (hashed credential, KEK-wrapped per ENG-0002), with an
  **OIDC/SSO seam later** (a future identity provider behind the same authz chokepoint, §5c).
- **Sessions:** issue a real session cookie / bearer after login. The Phase-1 token-in-query-param
  pattern ([Program.cs:88](../../../Program.cs#L88), and `?token=` for SSE
  [Program.cs:414](../../../Program.cs#L414)) leaks tokens into logs/history and **is removed**; SSE
  auth rides the authenticated session, not a query param.

### 5c. Console authz — fixed-role RBAC (decided, ENG-0008)

Console RBAC is **Decided (ENG-0008, 2026-06-16): fixed built-in roles over a pillar×verb catalog,
firm + early** — no longer "roles later / undecided."

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
