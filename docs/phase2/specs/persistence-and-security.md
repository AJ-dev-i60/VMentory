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

app_user                  -- only if local accounts (see §3)
  id, username, password_hash (Argon2id), role, created_at
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

## 5. Auth model

Phase-1 security — random free port, a 24-byte session token, **127.0.0.1 only**
([Program.cs:37](../../../Program.cs#L37) `UseUrls("http://127.0.0.1:…")`,
[Program.cs:84](../../../Program.cs#L84) token middleware, [Program.cs:528](../../../Program.cs#L528)
`GenerateToken`) — **does not survive** becoming a shared hosted service
([ARCHITECTURE.md §5](../ARCHITECTURE.md#5-security-this-is-now-a-hosted-service-not-loopback)).
It assumes a single local operator on loopback; Phase 2 is reachable over the network by multiple
users. Replace it:

- **UI / user auth:** at minimum a **configured admin credential** (2.0 exit:
  [ROADMAP.md §2.0](../ROADMAP.md) "Replace session-token/loopback with a configured admin
  login"); **scoped roles** in 2.2 and **OIDC/SSO optional** ([ROADMAP.md §2.2](../ROADMAP.md)). Local
  account passwords hashed with **Argon2id**, never the Phase-1 single shared token.
- **Console RBAC is its own open topic ([ENG-0008](../../engineering/discussions/0008-rbac-scoped-console-auth.md), Open).**
  The role set (e.g. **backup-operator / vm-operator / admin**) and how roles gate write verbs/pillars
  are **undecided** — the `app_user.role` column (§1) and per-verb authorization depend on it. This
  console RBAC is **distinct from** the agent's constrained-verb authz (ENG-0004): RBAC decides
  *which operator* may invoke *which pillar/verb*; the agent independently fixes *what verbs exist at
  all*. To be decided before 2.2 auth hardening; until then `role` is a placeholder. *Proceed against
  the current recommendation (a small fixed role set), noting the dependency.*
- **Sessions:** issue a real session cookie / bearer after login. The Phase-1 token-in-query-param
  pattern ([Program.cs:88](../../../Program.cs#L88), and `?token=` for SSE
  [Program.cs:414](../../../Program.cs#L414)) leaks tokens into logs/history and must go for the
  UI. SSE auth then rides the authenticated session, not a query param.
- **Core ↔ Agent:** **mutual mTLS** (decided, ENG-0004 — not "or signed tokens"); the agent accepts
  only the Core's identity ([agent-protocol.md §3](agent-protocol.md#3-authentication-trust--enrollment-decided-eng-00030005)).
- **Provider secrets:** from `ISecretStore`, scoped (§4).
- **Transport:** the browser↔Core hop is HTTPS, not the Phase-1 plain-HTTP-on-loopback.

### Agent PKI — private CA in Core (decided, ENG-0005)

The mTLS that secures Core↔Agent is backed by a **private CA inside Core** (external-CA / AD CS seam
deferred). This is a separate trust domain from the operator-facing dashboard TLS.

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
  allow/deny revocation, no CRL/OCSP (ENG-0005).

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
5. **Console RBAC / scoped roles** ([ENG-0008](../../engineering/discussions/0008-rbac-scoped-console-auth.md),
   **Open**) — the role set (backup-operator / vm-operator / admin) and the verb-authorization model
   the `app_user.role` column (§1) feeds. Distinct from agent authz (ENG-0004). To be decided before
   2.2 auth hardening; the schema reserves `role` but the values/semantics are not yet pinned.
