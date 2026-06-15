# Persistence & Security

> Component spec · expands [ARCHITECTURE.md §3 Persistence](../ARCHITECTURE.md#3-persistence-hybrid)
> and [§5 Security](../ARCHITECTURE.md#5-security-this-is-now-a-hosted-service-not-loopback).
> Anchored to **Decision 2**: hybrid state — persist registry/jobs/history; **secrets in a
> vault/secret store, never plaintext at rest**.

Phase 1 is ephemeral by design: in-memory `Store` ([Store.cs:5](../../../Store.cs#L5)), purge on
quit ([Program.cs:489](../../../Program.cs#L489), [Program.cs:502](../../../Program.cs#L502)),
errors-only log with no PII ([Program.cs:561](../../../Program.cs#L561) `ErrorLogger`). Phase 2 is
a hosted, multi-platform, **read/write** service — it must remember hosts, history, and jobs, and
it must do real auth. This spec defines exactly what is persisted, what is not, and how secrets and
access are handled.

---

## 1. Schema sketch

Datastore: **SQLite by default** (single file in a mounted volume), **Postgres opt-in** for
multi-instance, via **EF Core** ([ARCHITECTURE.md §3](../ARCHITECTURE.md#3-persistence-hybrid)).
Tables (sketch — column lists are indicative, not final):

```
provider_registration
  id, platform (HyperV|Proxmox), display_name,
  endpoint (agent address | PVE node/cluster URL),
  secret_ref         -- POINTER into the secret store, NOT a secret
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

migration_job
  id, source_vm_ref, target_provider_id, mode (dry-run|run),
  status, created_by, created_at, updated_at

migration_step
  id, job_id -> migration_job, ordinal, name, depends_on,
  status (Pending|Running|Succeeded|Failed|Skipped|RolledBack),
  inputs_json, outputs_json, idempotency_key, started_at, finished_at

migration_step_log
  id, step_id -> migration_step, ts, level, message   -- structured, persisted

audit_event
  id, ts, actor (user), action, target_ref, result, detail_json

app_user                  -- only if local accounts (see §3)
  id, username, password_hash (Argon2id), role, created_at
```

**Inventory snapshots are the key Phase-1→2 upgrade.** Phase 1 computes a *session* diff in
memory ([Store.cs:75](../../../Store.cs#L75) `RecordDiff`, keyed `hostId:name`
[Store.cs:82](../../../Store.cs#L82)) and loses it on quit. Persisting `inventory_snapshot` turns
that into **real historical diff** ([ARCHITECTURE.md §3](../ARCHITECTURE.md#3-persistence-hybrid),
[ROADMAP.md §2.1](../ROADMAP.md) historical stats). **Keep the diff algorithm** from
[Store.cs:75](../../../Store.cs#L75) — it's correct — but feed it two persisted snapshots instead
of previous-vs-current in-memory lists, and resolve the identity caveat in
[provider-abstraction.md Open question 1](provider-abstraction.md#7-open-questions-need-a-human-decision)
before relying on cross-session diffs.

---

## 2. What is persisted

- Provider/host **registry** (addresses, display names, cached capabilities, **secret refs**).
- **Inventory snapshots** → historical view + diff.
- **Migration jobs, steps, step logs** ([migration-job-model.md §1](migration-job-model.md#1-why-a-persisted-dag)) —
  must survive restart for resume.
- **Audit trail** (§5).
- Local **user accounts** (hashed) if not using external IdP (§3).

## 3. What is NOT persisted (in the DB)

- **Credentials, API tokens, SSH keys.** Never in plaintext at rest — Decision 2. The DB stores a
  `secret_ref` pointer only; the secret itself lives in the secret store (§4). This is the hard
  line the ARCHITECTURE draws ([ARCHITECTURE.md §3](../ARCHITECTURE.md#3-persistence-hybrid)).
- No host/VM **PII in logs** beyond what audit explicitly records — continues the Phase-1
  `ErrorLogger` discipline of "errors only, no host/PII data"
  ([Program.cs:8](../../../Program.cs#L8), [Program.cs:561](../../../Program.cs#L561)).

---

## 4. Secret handling

- **Source of secrets:** Docker secrets / env / an external vault
  ([ARCHITECTURE.md §3](../ARCHITECTURE.md#3-persistence-hybrid), [ROADMAP.md §2.0](../ROADMAP.md)).
  The DB holds only the `secret_ref`; resolution happens at use time.
- **In memory:** secrets are decrypted into memory only, held for the operation, and zeroed. The
  Phase-1 discipline **carries forward verbatim** — `Credentials` holds the password as `byte[]`
  and `Array.Clear`s it on dispose ([Models.cs:121](../../../Models.cs#L121),
  [Models.cs:143](../../../Models.cs#L143)); `Store` disposes creds on host removal and on purge
  ([Store.cs:41](../../../Store.cs#L41), [Store.cs:133](../../../Store.cs#L133)). Reuse this type
  for PVE tokens and SSH key material, not just WinRM passwords.
- **Scope secrets minimally:** PVE **API tokens privilege-separated**, not a root ticket
  ([proxmox-integration.md §1](proxmox-integration.md#1-authentication),
  [ARCHITECTURE.md §5](../ARCHITECTURE.md#5-security-this-is-now-a-hosted-service-not-loopback));
  agent trust via **mTLS / short-lived signed tokens**
  ([agent-protocol.md §3](agent-protocol.md#3-authentication--trust)).

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
  login"); **roles** in 2.2 and **OIDC/SSO optional** ([ROADMAP.md §2.2](../ROADMAP.md)). Local
  account passwords hashed with **Argon2id**, never the Phase-1 single shared token.
- **Sessions:** issue a real session cookie / bearer after login. The Phase-1 token-in-query-param
  pattern ([Program.cs:88](../../../Program.cs#L88), and `?token=` for SSE
  [Program.cs:414](../../../Program.cs#L414)) leaks tokens into logs/history and must go for the
  UI. SSE auth then rides the authenticated session, not a query param.
- **Core ↔ Agent:** mTLS or short-lived signed tokens; the agent accepts only the Core's identity
  ([agent-protocol.md §3](agent-protocol.md#3-authentication--trust)).
- **Provider secrets:** from the secret store, scoped (§4).
- **Transport:** the browser↔Core hop is HTTPS, not the Phase-1 plain-HTTP-on-loopback.

---

## 6. Audit log

Every **write / management / migration** action is recorded
([ARCHITECTURE.md §5](../ARCHITECTURE.md#5-security-this-is-now-a-hosted-service-not-loopback),
[ROADMAP.md §2.2](../ROADMAP.md) + [cross-cutting](../ROADMAP.md#cross-cutting-every-milestone)).
Schema: `audit_event` (§1) — `ts, actor, action, target_ref, result, detail_json`. Read-only
inventory scans (the only Phase-1 operation) need not be audited; lifecycle ops, credential
changes, provisioning, and every migration step do. Audit rows are **append-only** and must not
contain secret values.

---

## 7. Open questions (need a human decision)

1. **Multi-tenant vs single-operator.** The open ARCHITECTURE question
   ([ARCHITECTURE.md open questions](../ARCHITECTURE.md#open-questions-for-the-spec-agents)) — it
   sets auth depth (do we need per-tenant data isolation and row-level scoping, or just roles for
   one org?). This decides whether `provider_registration`/`host`/`audit_event` carry a `tenant_id`
   from day one. **Owner decision — schema-shaping, decide before 2.0 persistence lands.**
2. **Which secret store is the v1 target** — Docker secrets only (simplest, 2.0), or commit to a
   vault (HashiCorp Vault / cloud KMS) interface up front? An abstraction (`ISecretStore`) lets us
   start with Docker secrets/env and add a vault later; confirm that's acceptable.
3. **Snapshot retention / cadence.** How often to snapshot inventory and how long to retain
   (storage vs history depth)? Affects DB growth, especially on SQLite. Needs an operator-facing
   policy.
4. **At-rest encryption of the DB itself.** The DB holds no plaintext secrets (§3), but inventory
   and audit data may be sensitive. Encrypt the SQLite file / require encrypted Postgres, or treat
   the host volume as the trust boundary? **Owner decision.**
