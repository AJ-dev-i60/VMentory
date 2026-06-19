# ENG-0011 — Observability, logging & failure-surfacing

**Status:** Open (Raised) — broader observability contract for discussion and planning; **NOT to be
decided yet** (owner instruction). **One sub-fork resolved:** the **UI-facing failure-classification /
health-tier model (ENG-0011a)** is **Decided 2026-06-19** (see *Decision — ENG-0011a* at the bottom).
The rest (logging substrate Fork 1, sinks/retention Fork 2/6, audit-vs-ops Fork 3, per-transport
envelope Fork 5, config Fork 8) stays **plan-only / undecided**.
**Raised:** 2026-06-17 by human (owner)
**Affects:** `Program.cs` (`ErrorLogger`, `DevLog`, `AppConfig`, scan/add-host failure paths), `EventHub.cs`, `Reachability.cs`/`Scanner.cs`, the future ops/migration engine, `docs/phase2/specs/persistence-and-security.md` (§1 schema, §6 audit, §7 retention/encryption), `docs/phase2/specs/migration-job-model.md` (§6 per-step logging), ROADMAP slice ordering
**Related:** ENG-0008 (single audited authz chokepoint / `audit_event`), ENG-0009 (Proxmox REST + SSH transports — failure-prone surfaces), ENG-0010 (container/stdout capture + env config contract), ENG-0002 (secrets never logged), ENG-0001/0004/0005 (HV agent gRPC/mTLS transport, later), `migration-job-model.md` §6, `persistence-and-security.md` §6/§7, the `migrate-vm` skill (validated failure-prone runbook)

## Context

The trigger is concrete and current. The owner stood up the containerized dev Core on Coolify
(re-baselined slice (1), ENG-0010) and tried to add Hyper-V hosts with credentials. **Every host came
back "unreachable" with no indication of *why*.** The true cause is architectural: a Linux container
has no PowerShell/WinRM host and no network line-of-sight to the HV hosts — exactly the surfaces the
re-baseline `IsWindows()`-guards and defers to the HV agent slice (PROGRESS §5). But the UI and logs
surfaced only a generic "unreachable," giving the operator no path from symptom to cause.

That opacity *is the point being raised*: **"Many of the tasks this platform performs are
failure-prone and will fail silently."** The owner wants a logging/observability/failure-surfacing
system **designed before** the failure-prone surfaces land at scale: Proxmox REST + SSH (ENG-0009),
the general operations / migration job engine (`migration-job-model.md`), HV agent comms
(ENG-0001/0004), scheduling, and data-movement-at-scale (PROGRESS §1 "new shared subsystems").

### What exists today (ground truth)

- **`ErrorLogger`** — bespoke append-to-file, "errors only, no PII"
  ([Program.cs:714](../../../Program.cs#L714)). Just hardened (commit `ca196f4`): writes to a writable
  path (`VMENTORY_LOG` → `VMENTORY_DB` data dir → app base, [Program.cs:667](../../../Program.cs#L667)
  `ResolveLogPath`) and is **fail-safe** — a locked/bad path degrades to a no-op or temp fallback and
  never crashes startup ([Program.cs:721](../../../Program.cs#L721)). This is the **only persistent log
  today** and it is thin: a flat string + optional exception, no levels, no structure, no correlation.
- **`DevLog`** — console step/ok/warn/err helpers ([DevLog.cs:8](../../../DevLog.cs#L8)) gated by
  `Verbose` (set from `--verbose`, [Program.cs:47](../../../Program.cs#L47)). **Dev-only, ephemeral,
  captured nowhere.** It already does targeted redaction (`RedactPassword`,
  [DevLog.cs:66](../../../DevLog.cs#L66)) and per-transport tagging (`[ICMP]`/`[TCP]`/`[WINRM]`/`[AUTH]`
  in [Reachability.cs](../../../Reachability.cs)) — useful prior art, but it evaporates.
- **Failure surfacing is ad-hoc.** Per-host string fields `AddError`
  ([Program.cs:264](../../../Program.cs#L264), `:292`, `:309`, `:342`, `:353`) and `ScanError`
  ([Program.cs:426](../../../Program.cs#L426), `:460`, `:487`) are written onto the `Host` model and
  shown in the UI. The "unreachable" message the operator saw is literally
  `"Host unreachable (ICMP failed)"` / `"WinRM port not responding"`
  ([Program.cs:292](../../../Program.cs#L292)) — no capability context, no "this Core build can't reach
  Hyper-V over WinRM from a Linux container." There are **no structured records, no correlation IDs, no
  history, nothing queryable.**
- **Scans are fire-and-forget `Task.Run`** ([Program.cs:420](../../../Program.cs#L420) region) — an
  unhandled exception is caught only at the outer boundary ([Program.cs:486](../../../Program.cs#L486))
  and otherwise can vanish silently.
- **`EventHub`** (SSE over `System.Threading.Channels`, [EventHub.cs:10](../../../EventHub.cs#L10))
  already streams typed events to the browser (`Broadcast(type, data)`) — the existing live-progress
  transport (scan progress/complete) and an obvious candidate live-log channel.
- **Deployment is now a container** (ENG-0010). Coolify/Docker capture container **stdout/stderr** —
  that is exactly how the owner read the crash-loop. So **12-factor stdout logging** is now a
  first-class, already-wired sink (the platform tails it for free), which reframes the file-vs-DB
  question.

### What the specs already assume (so we don't re-invent or contradict)

- `migration-job-model.md` §6 already requires **"structured, persisted logs"** per step, surviving
  restart, with **live progress reusing `EventHub`** exactly as Phase-1 scan progress does. The job
  engine already carries job/step identity (the DAG, idempotency keys, §3) — i.e. correlation is
  *already in the design* for the migration pillar; ENG-0011 should generalize it, not fork it.
- `persistence-and-security.md` §6 defines an **`audit_event`** table
  (`ts, actor, action, target_ref, result, detail_json`, append-only, **never** secret values), with
  `ISecretStore` emitting an audit event per access (ENG-0002) and the agent logging every executed
  verb back to Core (ENG-0004). ENG-0008 makes the **single audited authz chokepoint** mandatory before
  any write verb. So **audit already has a home**; the open design question is its *relationship* to
  operational logging, not whether audit exists.
- `persistence-and-security.md` §7 already has **two still-open owner questions** ENG-0011 must dock
  onto: **(1) snapshot retention/cadence** (DB-growth bound) and **(2) at-rest DB encryption**
  ("inventory and audit data may be sensitive"). Persisted logs/audit are *more* of the same sensitive,
  unbounded-growth data — ENG-0011 cannot answer sink/retention in isolation from §7.

## The question

What is VMentory's **observability contract** — how the platform produces, correlates, persists,
surfaces, and bounds diagnostic signal — so that failure-prone operations (transport, ops/migration
steps, agent comms, scheduling, data movement) **fail loudly and traceably** instead of silently?

Concretely, several distinct forks need owner direction:

1. **Logging substrate:** keep/extend the bespoke `ErrorLogger`/`DevLog`, or adopt
   `Microsoft.Extensions.Logging` (`ILogger<T>`) with a structured sink?
2. **Primary sink:** stdout-first (JSON, platform-captured per ENG-0010) vs file vs SQLite-persisted
   log table — and which combination.
3. **Failure-surfacing path:** the consistent capture → correlate → surface (UI via `EventHub`?) →
   persist pipeline, including **capability-aware** messages (the "unreachable" example).
4. **Audit vs operational logging:** one subsystem or two? (`audit_event` already exists per ENG-0008.)
5. **How much, and what, to surface in the UI** vs keep for after-the-fact diagnosis.

## Option space (forks, with honest trade-offs — analysis only, no pick)

### Fork 1 — Logging substrate: bespoke vs `Microsoft.Extensions.Logging`

**A1 — Extend the bespoke `ErrorLogger`/`DevLog`.** Add levels + a structured record type to what
exists.
- **Pros:** zero new dependency (matters for the offline-restore constraint, CLAUDE.md gotcha 5/3);
  already fail-safe and redaction-aware; smallest diff.
- **Cons (accepts):** we re-implement leveling, scopes, structured/JSON formatting, and per-component
  filtering that `ILogger` gives for free; every new subsystem (Proxmox client, SSH executor, ops
  engine, agent channel) must remember to call our bespoke API rather than inject a standard `ILogger<T>`;
  no ecosystem sinks. We accept ongoing maintenance of a logging framework that is not our product.

**A2 — Adopt `Microsoft.Extensions.Logging` (`ILogger<T>`) + a structured sink.** ASP.NET Core already
configures it; formalize `VerboseMode` as log *levels* and add a JSON console formatter (built into
`Microsoft.Extensions.Logging.Console`, no third-party dep) and/or a custom SQLite sink.
- **Pros:** standard injection everywhere (`ILogger<ProxmoxClient>`, `ILogger<SshExecutor>`,
  `ILogger<MigrationEngine>`); structured state + scopes give correlation IDs natively
  (`BeginScope(jobId/correlationId)`); built-in JSON console formatter satisfies the 12-factor
  stdout-first posture (Fork 2) with no extra package; per-component log levels via the standard config
  surface map cleanly onto ENG-0010's env contract (`Logging__LogLevel__*`); `DevLog`'s `--verbose`
  becomes a level switch, not a parallel system.
- **Cons (accepts):** a migration of existing call sites; `EventHub`-as-log-transport and
  SQLite-persistence are custom `ILoggerProvider` work we still write (but as small, standard sinks);
  must ensure the structured pipeline keeps the existing redaction discipline (Fork 6).

### Fork 2 — Primary sink: stdout-first vs file vs DB-persisted

**B1 — stdout-first (JSON), platform-captured.** Core writes structured JSON to stdout/stderr; Coolify/
Docker/journald own capture, rotation, retention, and shipping.
- **Pros:** 12-factor; **already working** (it is how the owner read the crash-loop); no in-app rotation
  or DB-growth concern; aggregator-friendly (Loki/ELK later). Aligns with ENG-0010's "the platform owns
  the runtime" framing.
- **Cons (accepts):** logs are only as durable as the platform's capture (ephemeral if the container is
  recreated and nothing ships them); **not queryable from inside the product** — the operator can't see
  "why did host X fail last Tuesday" in the VMentory UI without an external stack; survives only as long
  as the orchestrator keeps it.

**B2 — File sink (today's `ErrorLogger` model).** Append to a file on the `/data` volume.
- **Pros:** durable across container restarts (on the mounted volume); zero external dependency; already
  exists and is fail-safe.
- **Cons (accepts):** we own rotation/retention (none today → unbounded growth on the same volume as the
  DB); not queryable/structured as-is; duplicates what the platform already captures from stdout in a
  container deployment — arguably the *wrong* default for ENG-0010 while still the right default for the
  Windows desktop exe (Phase-1 still ships).

**B3 — SQLite-persisted log/event table.** Operational records (and/or audit) in the EF Core DB, like
inventory snapshots.
- **Pros:** **queryable inside the product** (the failure-surfacing UI, post-mortem, per-host/per-job
  history); correlates naturally with `audit_event`, jobs, and snapshots in one store; survives restart;
  the engine already persists per-step logs by design (`migration-job-model.md` §6).
- **Cons (accepts):** **DB growth** — directly collides with the open §7 retention question and SQLite's
  practical write-volume ceiling (high-frequency transport/debug logs do **not** belong here); needs a
  retention/pruning policy and a level threshold (e.g. only Warning+ and operation-step records persist,
  not every debug line); write-amplification on the same SQLite file as inventory.

> These are not mutually exclusive. The realistic design is a **tiered sink**: stdout-first for the
> firehose (B1), a **bounded** SQLite table for the subset that must be queryable in-product (B3 —
> operation-step logs + warnings/errors + audit), and the file (B2) retained mainly for the Phase-1
> desktop exe. The fork to decide is the **default and the boundary** (what gets persisted vs only
> streamed to stdout), which is inseparable from §7 retention.

### Fork 3 — Audit log vs operational log: unified or separate

**C1 — Separate subsystems.** `audit_event` stays a distinct, append-only, security-grade table
(ENG-0008/§6); operational logs are their own pipeline (stdout + bounded SQLite).
- **Pros:** audit keeps its strict guarantees — append-only, tamper-evident posture, "every allow/deny,
  never secrets," different retention (audit likely kept *longer* and pruned by different rules than
  debug logs); clean security story; matches the schema already specced.
- **Cons (accepts):** two writers and two query surfaces; a single operator action (e.g. "migrate VM X")
  produces a row in *both* (the audit decision + the operational step trail) and the UI must stitch them
  by correlation ID; some duplication of plumbing.

**C2 — One unified event store** with an `is_audit`/`kind` discriminator and one query surface.
- **Pros:** one writer, one timeline, one UI; correlation is trivial (same table, same `correlation_id`).
- **Cons (accepts):** **muddies audit's guarantees** — mixing high-volume operational logs into the
  append-only security record complicates retention (you can't prune debug rows freely without risking
  the audit subset), tamper-evidence, and the "never secrets" invariant (operational logs are far more
  likely to *accidentally* carry sensitive detail than the curated `audit_event.detail_json`). ENG-0008
  treats audit as a security control, not a log level — folding them risks weakening it.

> Leaning signal (not a decision): **C1 with a shared correlation ID.** Audit is a security control with
> its own lifecycle (ENG-0008/§6); operational logging is diagnostics. Keep them separate tables but make
> them *joinable* on `correlation_id`/`job_id` so the UI can present one story. To be confirmed by owner.

### Fork 4 — Failure-surfacing pipeline & capability-aware messaging

The consistent path for a failure-prone op: **capture** (typed failure, not a bare string) →
**correlate** (attach correlation/job/host IDs) → **classify** (transient vs terminal; cause category)
→ **surface** (UI via `EventHub` broadcast + the per-entity error field) → **persist** (bounded sink for
post-mortem).

- **D1 — Typed failure model.** Replace the ad-hoc `AddError`/`ScanError` strings
  ([Program.cs:292](../../../Program.cs#L292) etc.) with a structured failure record (code, category,
  human message, remediation hint, correlation ID) that flows to UI **and** sink uniformly.
  - **Pros:** the "unreachable" case becomes a *classified* failure — e.g.
    `HV_TRANSPORT_UNAVAILABLE: this Core build cannot reach Hyper-V over WinRM from a Linux container
    (no PowerShell host; HV agent slice not yet shipped)` — actionable, not generic. Capability-aware
    messages fall out of the `ProviderCapabilities` model already in Core (the provider can declare *why*
    it can't, not just *that* it can't).
  - **Cons (accepts):** more design up front (a failure taxonomy/code catalog) and discipline to route
    all failures through it; risk of over-engineering if kept too granular. The taxonomy must be
    maintained as transports grow.
- **D2 — Keep strings, just log more.** Minimal: keep `AddError`/`ScanError`, add a structured log line
  beside them.
  - **Pros:** smallest change; UI untouched.
  - **Cons (accepts):** does **not** fix the trigger — the UI still shows a generic string with no cause,
    no remediation, no correlation to a queryable record; "fails silently" persists at the surface that
    actually matters to the operator.

> The trigger specifically indicts the *surface* ("the UI just says unreachable"), so the
> failure-surfacing pipeline (Fork 4) is arguably the highest-value part of this topic, independent of
> which logging library (Fork 1) backs it.

### Fork 5 — Per-transport diagnostics (the failure-prone surfaces)

Each transport needs first-class, capturable diagnostics — this is where "fails silently" bites hardest:

- **Proxmox REST** (ENG-0009): request/response status, the **UPID** for every write + the polled
  `tasks/{upid}/status` trail (proxmox-integration.md §2) — a natural correlation handle to log per op.
- **Proxmox SSH** (ENG-0009): command issued (redacted), exit code, stdout/stderr capture — the
  `migrate-vm` skill's `qm importdisk`/`qemu-img` steps are exactly the failure-prone, long-running ops
  that must stream + persist their output (`migration-job-model.md` §6 already tails step output).
- **HV agent** (ENG-0001/0004, later): gRPC/mTLS call + verb result; the agent already "logs every
  executed verb back to Core" (§6) — define how that lands in the log/audit stores.
- **WinRM/PowerShell** (Phase-1 path, [Reachability.cs](../../../Reachability.cs)): stderr capture +
  the existing `[AUTH]`/`[WINRM]` tagging, promoted from ephemeral `DevLog` to a real captured channel.

The fork: a **uniform per-transport diagnostic envelope** (so every transport reports cause the same
way, feeding Fork 4's taxonomy) vs per-transport bespoke logging. Uniform is more design but is what
makes "loud, traceable failure" consistent across a growing transport set.

### Fork 6 — Sinks, persistence, retention (docks onto §7)

Whatever persists must answer the **two already-open §7 owner questions** at the same time:
- **Retention/rotation/growth bound** for any persisted log/audit (analogous to §7 snapshot
  retention) — different rules likely for debug logs (short/none), operation-step logs (per-job, pruned
  with the job), warnings/errors (medium), and audit (long, ENG-0008).
- **At-rest sensitivity** (§7 #2) — operational logs and audit may carry sensitive (non-secret) detail;
  they ride the same DB/volume trust-boundary decision. ENG-0011 should be **decided together with §7**,
  not ahead of it, to avoid contradicting the eventual encryption/retention posture.

### Fork 7 — Secrets / PII hygiene (a hard constraint, not really an option)

Carry forward Phase-1's "no PII / no secrets in logs" stance (`persistence-and-security.md` §6: audit
rows **never** contain secret values; ENG-0002: `ISecretStore` is the only place secrets live).
**Never log:** credentials, API tokens, SSH keys, the KEK/DEK, PVE API tokens, cert private keys. The
existing `DevLog.RedactPassword` ([DevLog.cs:66](../../../DevLog.cs#L66)) is the seed; a structured
pipeline needs a **central redaction/allow-list** so structured fields can't accidentally serialize a
secret (e.g. never log a whole `Credentials`/secret object; log a `secret_ref`, never a value). This is
a binding constraint on Forks 1–6, not a choice.

### Fork 8 — Config surface (consistent with the ENG-0010 env contract)

Log **level / format / sink** via env, matching ENG-0010's env-driven runtime (`VMENTORY_HTTP_*`,
`VMENTORY_DB`, `VMENTORY_LOG`). With `ILogger` (A2) this is the standard `Logging__LogLevel__<Category>`
surface for free; per-component verbosity (e.g. quiet the poller, verbose the SSH executor) becomes
config, not code. The fork is mainly *naming/consistency* (reuse `VMENTORY_LOG`* prefixes vs the
standard `Logging__*` keys vs both) — small, but worth settling so the container's env contract stays
coherent.

## Open sub-questions

- **Default sink for the container vs the desktop exe.** Phase-1 still ships as a Windows desktop exe
  (file sink makes sense there); ENG-0010 Core is a container (stdout-first makes sense there). One
  default with overrides, or environment-detected? (Mock mode writes nothing — must stay true,
  CLAUDE.md gotcha 7.)
- **What persists in SQLite vs only streams to stdout** — the level/category boundary (Fork 2/6). Tie to
  §7 retention before fixing.
- **Audit/ops correlation key** — a single `correlation_id` spanning a user request → ops-engine job →
  agent/SSH verbs → audit rows. Where is it minted (the authz chokepoint, ENG-0008?) and how is it
  threaded through `EventHub` and the job DAG?
- **Failure taxonomy ownership** — does the failure-code catalog live in `VMentory.Core` next to
  `ProviderCapability`, so providers declare both capabilities and failure modes? (Capability-aware
  messaging suggests yes.)
- **`EventHub` as a log transport** — broadcast logs/failures to all subscribers (simple) vs a scoped
  channel; and the interaction with RBAC (ENG-0008) — should `view-audit`/log visibility be a
  permission, so a Viewer doesn't see another operator's failure detail?
- **Live exercise needed:** the trigger (Coolify "unreachable") should be reproduced and the *desired*
  end-state message written out, to anchor the failure-taxonomy design in the real first failure. (Data
  point we can't fully derive without the live container + a real HV host.)
- **Retire/relate `DevLog` and `ErrorLogger`** — does ENG-0011 deprecate both in favor of `ILogger`
  (A2), or keep `ErrorLogger` as the desktop-exe file sink and absorb `DevLog` into log levels?
- **Ordering vs the slice plan** — where does the observability substrate land relative to slices
  (2) login+RBAC → (3) `ISecretStore` → (4) Proxmox read → (5) write verbs? The audit chokepoint
  (ENG-0008) is slice (2)/(5); the per-transport diagnostics want to exist *with* the Proxmox transports
  (slice (4)/(5)). Argues for the substrate (Fork 1/2) early and the transport diagnostics (Fork 5) with
  each transport.

## Decision

> _Broader observability contract: pending owner discussion._ Raised for discussion and planning only;
> no decision across Forks 1–8 is recorded yet. **Exception:** the UI-facing failure-classification /
> health-tier sub-fork is resolved below as **ENG-0011a**. <!-- on resolution of the rest: date · choices across Forks 1–8 · rationale · consequences; coordinate retention/encryption resolution with persistence-and-security.md §7 -->

---

## Decision — ENG-0011a (UI-facing failure classification & health tiers)

**Status:** **Decided** · **2026-06-19** · owner.
**Scope boundary (read first):** this resolves *only* how a host's reachability/scan signals are
**classified into a health tier and surfaced to the dashboard**, plus the typed model and provider
contract change that makes that classification authoritative server-side. It deliberately does **NOT**
decide the rest of ENG-0011: the logging substrate (Fork 1 bespoke vs `ILogger`), the persisted
sinks/retention (Fork 2/6, still gated on `persistence-and-security.md` §7), audit-vs-ops separation
(Fork 3), the uniform per-transport diagnostic *envelope* (Fork 5), and the env config surface (Fork 8)
all remain **Open / plan-only**. ENG-0011a is the failure-*surfacing* slice of Fork 4 (the D1 "typed
failure model" option), pulled forward because it is the trigger the owner actually hit and because it
unblocks the dashboard UI rebuild. The `HostFault` taxonomy decided here is the seed the broader Fork 4/5
taxonomy will extend; it is intentionally narrow (reachability + scan stages), not the whole platform
failure catalog.

### What was decided

#### 1. Six health tiers + a fixed severity ordering

The single dashboard status per host is one of six tiers. A host can have multiple faults across stages;
the **displayed tier is the highest meaningful tier among its faults** (a *worst-wins* fold), except
`Unknown` which is a transient "we don't know yet" state, not a severity peak.

| Tier | Meaning | Colour intent |
|------|---------|---------------|
| `Healthy` | Management plane reachable, auth OK, last scan succeeded (or not yet scanned but reachable). | green |
| `Degraded` | Works but limited — reachable + authenticated, but something is wrong: **PVE 403** (valid token, missing privilege), a scan that failed *after* a good connect, or a partial-inventory condition. Amber, with a remediation hint where high-confidence. | amber |
| `Unauthorized` | Credentials are present but **rejected** — wrong/expired token or bad WinRM creds (**PVE 401**, HV WinRM Access-Denied). Remediation = *fix the credential*. | red |
| `Unreachable` | Cannot reach the management plane at all — DNS unresolved, mgmt TCP port closed, or transport error before auth. Remediation = *fix DNS/network/firewall*. | red |
| `Unconfigured` | Host is **reachable but has no credentials set** — a *setup* state, not a failure. Distinct so onboarding reads as "finish setup," not "broken." | grey/blue (setup) |
| `Unknown` | Mid-check, never polled, or `Connecting`. Not a failure; a "checking…" state. | grey/spinner |

**Severity ordering for the worst-wins fold (highest first):**
`Unreachable > Unauthorized > Degraded > Unconfigured > Healthy`, with `Unknown` used whenever the host
is connecting / has never been evaluated. Rationale for the order: a host you can't reach is the most
blocking; a wrong credential (fix creds) outranks a privilege gap (Degraded, works-but-limited) because
the former blocks *everything* and the latter blocks *some* verbs; `Unconfigured` sits just above
`Healthy` because it is benign-but-actionable setup, not an error.

**Two deliberate splits vs today's UI** (which only has `ok/warn/bad/unk`,
[index.html:1413](../../../wwwroot/index.html#L1413)):
- **Unauthorized split from Degraded** — remediation differs: *fix credentials* vs *fix network/grant a
  role*. Collapsing them (as today) sends the operator down the wrong path.
- **Unconfigured split out as a non-failure** — a reachable host with no creds is setup-in-progress, not
  red. Today there is no such state; a credential-less host falls through to a generic failure string.

#### 2. Signal → tier mapping per chain stage, with stable CODE strings

The reachability/scan chain is evaluated stage by stage. Each stage can emit a `HostFault` with a
**stable code** (the code string is the contract — UI/design and any future log query key off it, never
the human text). ICMP is the one stage that emits **no fault and never affects the tier**.

| Stage (`FailureStage`) | Signal | Code | Tier contribution |
|------------------------|--------|------|-------------------|
| **DNS** | name does not resolve | `DNS_UNRESOLVED` | `Unreachable` |
| **ICMP** | ping reply / no reply | *(none — informational only)* | **never** affects tier; surfaced as `icmpReplied` for a greyed P badge only |
| **TCP mgmt port** | mgmt port closed/refused (HV 5985, PVE 8006) | `MGMT_PORT_CLOSED` | `Unreachable` (hint populated) |
| **Mgmt transport** | TLS/socket/timeout error reaching the mgmt API before an auth verdict | `MGMT_TRANSPORT_ERROR` | `Unreachable` |
| **Mgmt auth** | credentials rejected — **PVE 401**, HV WinRM Access-Denied | `AUTH_REJECTED` | `Unauthorized` |
| **Mgmt auth** | authenticated **but under-privileged** — **PVE 403** | `AUTH_INSUFFICIENT_PRIV` | `Degraded` (hint populated) |
| **Credentials present** | host reachable, **no credential configured** | `NO_CREDENTIALS` | `Unconfigured` (hint populated) |
| **Inventory scan** | scan failed *after* a good connect | `SCAN_FAILED` | `Degraded` |

Notes that pin the platform-specific behaviour:
- **PVE 401 vs 403 must be split server-side.** Today both collapse into one string:
  `"API token rejected — check token ID and secret"` at
  [ProxmoxProvider.cs:45-47](../../../ProxmoxProvider.cs#L45) (QuickConnect) and `"API token rejected"`
  at [ProxmoxProvider.cs:92-94](../../../ProxmoxProvider.cs#L92) (Scan). The
  `catch (HttpRequestException ex) when (… == 401 || … == 403)` filter must be split into two arms:
  **401 → `AUTH_REJECTED` (Unauthorized)**, **403 → `AUTH_INSUFFICIENT_PRIV` (Degraded)**. This matches
  CLAUDE.md gotcha #13 (401 = wrong realm/token-id, 403 = valid-but-unprivileged) — the knowledge exists,
  it just isn't surfaced.
- **HV WinRM** distinguishes *Access-Denied* (→ `AUTH_REJECTED`) from *transport timeout / connect
  failure* (→ `MGMT_TRANSPORT_ERROR`) by matching the well-known WinRM error substrings. Today
  `Reachability.TestWinRmAuth` passes the raw PowerShell `AUTH_FAIL:$m` message through verbatim
  ([Reachability.cs:136-140](../../../Reachability.cs#L136)) and a timeout becomes the literal
  `"Timeout"` ([Reachability.cs:144-145](../../../Reachability.cs#L145)) — the substrings are available
  but unclassified. The provider owns turning those substrings into the two codes (see §4).
- **`NO_CREDENTIALS`** is the `Unconfigured` driver: reachable mgmt port but no resolvable credential.
  This is the one case that maps to a *non-failure* tier.

#### 3. The typed model (lives in `VMentory.Core`)

Replaces the loose `Reachability` booleans + the three parallel free-text error channels. New types in
`VMentory.Core` (alongside `ProviderCapability`/`PlatformKind`, per the Fork-4 open sub-question
"taxonomy ownership = yes, in Core"):

```csharp
enum HealthTier  { Unknown, Healthy, Unconfigured, Degraded, Unauthorized, Unreachable }
enum FailureStage { Dns, Icmp, TcpPort, Transport, Auth, Credentials, Scan }

// One classified fault at one stage. Code is the stable contract; Human is display text;
// Hint is a nullable high-confidence remediation; At is when it was observed.
record HostFault(string Code, HealthTier Tier, FailureStage Stage,
                 string Human, string? Hint, DateTimeOffset At);

// The composed per-host health: worst-wins Tier over Faults, plus the informational ICMP bit.
class HostHealth {
    HealthTier Tier;            // worst-wins fold of Faults (Unknown while connecting)
    List<HostFault> Faults;     // full per-stage fault list — the drill-down, from day one
    bool IcmpReplied;           // informational only; never folded into Tier
    DateTimeOffset CheckedAt;
}
```

**What it replaces / augments:**
- The loose booleans on `Reachability` — `Icmp`, `WinRm`, `Auth` (`AuthState`), `ErrorDetail`
  ([Models.cs:11-17](../../../VMentory.Core/Models.cs#L11)) — are superseded as the *source of truth for
  UI severity*. `IcmpReplied` carries the ICMP bit forward (badge only). `ErrorDetail` is replaced by
  the structured `Faults[]` (and note: `ErrorDetail` is **never actually rendered** by the UI today, so
  nothing visible is lost).
- The **three parallel free-text channels** are unified into `Faults[]`:
  `AddError` (add-host path, [Program.cs:264](../../../Program.cs#L264)ff),
  `ScanError` (scan path, [Program.cs:426](../../../Program.cs#L426)ff), and
  `Reachability.ErrorDetail` (provider path). One list, one classification, one render.
- **Back-compat / transition:** the old `Reachability` booleans and the `AddError`/`ScanError` strings
  are kept on the wire **only until** the dashboard renders `health.*`, then removed in the same UI
  rebuild slice. During the transition the evaluator is the single writer of both the new `HostHealth`
  and (derived from it) the legacy fields, so nothing reads a stale parallel value. **Exact cross-path
  removal sequencing is a flagged build-time sub-question** (below).

#### 4. Option A — full-depth provider contract change

The owner chose **Option A (full depth) now**: providers return a **typed health/fault result carrying a
full per-stage fault LIST**, not a single primary fault. The drill-down is the complete fault list from
day one.

- **`IVirtualizationProvider` shape change.** `QuickConnectAsync`/`ScanAsync` currently return
  `(bool Ok, string Error)` ([IVirtualizationProvider.cs:17,20](../../../VMentory.Core/IVirtualizationProvider.cs#L17)).
  They change to return a typed result that can carry `HostFault`s the provider is uniquely positioned to
  classify — i.e. the **platform-specific** ones the generic evaluator cannot know:
  - `ProxmoxProvider` emits `AUTH_REJECTED` (401) vs `AUTH_INSUFFICIENT_PRIV` (403) by splitting the
    merged `catch … when (401 || 403)` ([ProxmoxProvider.cs:45,92](../../../ProxmoxProvider.cs#L45)), and
    `SCAN_FAILED` on a post-connect scan error.
  - `HyperVProvider` (via `Reachability`) emits `AUTH_REJECTED` vs `MGMT_TRANSPORT_ERROR` by matching the
    well-known WinRM error substrings in the raw `AUTH_FAIL` message
    ([Reachability.cs:136-145](../../../Reachability.cs#L136)).
- **Generic stages stay in a single evaluator, not the providers.** DNS resolution, ICMP (informational),
  and the TCP mgmt-port probe are platform-independent and are composed by **one evaluator** that the
  **Poller** ([Poller.cs] 30s re-check) and the **add-host path** both call. The evaluator runs the
  generic stages, invokes the provider for the auth/scan stages, merges all faults, folds to a `Tier`,
  and produces one `HostHealth`.
- **Unify the two divergent classifiers.** Today the add-host path
  ([Program.cs:292](../../../Program.cs#L292): `"Host unreachable (ICMP failed)"` / `"WinRM port not
  responding"`) and the poller path classify reachability **independently with divergent strings**. Both
  must call the same evaluator so a host shows the same tier whether it was just added or just re-polled.
  This is the concrete fix for "two paths, divergent strings."

#### 5. The wire contract the design agent codes against

Each host in `/api/state` and every SSE broadcast (reusing the existing `EventHub` typed-event channel,
[EventHub.cs](../../../EventHub.cs) — **no new channel needed**) carries a `health` object:

```json
"health": {
  "tier": "Degraded",
  "icmpReplied": false,
  "checkedAt": "2026-06-19T10:22:31Z",
  "faults": [
    { "code": "AUTH_INSUFFICIENT_PRIV", "stage": "Auth",
      "human": "Token authenticated but lacks the required privilege",
      "hint": "Grant the token the PVEAuditor role (or the verb-specific role) on / in Proxmox" }
  ]
}
```

- `tier` is the server-composed worst-wins tier — **the dashboard renders this verbatim** and must not
  re-derive it.
- `icmpReplied` drives the greyed informational P badge only.
- `faults[]` is the full per-stage drill-down; each entry is `{ code, stage, human, hint }` (`hint`
  nullable). `code` is the stable key; `human` is display text.
- `checkedAt` is the evaluation time.
- This `health` JSON **is the contract that unblocks the dashboard UI rebuild** (the multi-platform
  dashboard proposals in `design/STATUS.md`): the dashboard can render tier + drill-down without
  re-implementing classification.

#### 6. UI must stop re-deriving severity

The three JS functions that independently re-derive severity from raw booleans must be replaced by
rendering the server-supplied `health.tier` / `health.faults`:
- `hostHealth(h)` ([index.html:1413](../../../wwwroot/index.html#L1413)) — returns `ok/warn/bad/unk` from
  `r.winRm`/`r.auth`/`scanState`. Replace with a tier→badge map over `h.health.tier`.
- `healthTip(h)` ([index.html:1426](../../../wwwroot/index.html#L1426)) — re-classifies for the tooltip.
  Replace with `h.health.faults` (human + hint).
- `subLine(h)` ([index.html:1484](../../../wwwroot/index.html#L1484)) — re-classifies again for the row
  subline. Replace with the tier/first-fault.

All three currently drift (e.g. each re-decides the PVE-port-closed vs unreachable wording separately).
`ErrorDetail` is **never rendered** today; the new `faults[]` is its replacement and is what the UI
actually shows. The design agent owns the visual treatment of the six tiers + the fault drill-down; this
record only fixes the data the UI consumes.

### Remediation hints (populated only high-confidence)

`hint` is in the contract from day one (design reserves space) but is **populated only for the
high-confidence cases**, null elsewhere:
- `AUTH_REJECTED` (PVE 401) → "Wrong token: check user@realm and token-id (gotcha #13)."
- `AUTH_INSUFFICIENT_PRIV` (PVE 403) → "Grant the token the required Proxmox role."
- `NO_CREDENTIALS` → "Set credentials for this host to finish setup."
- `MGMT_PORT_CLOSED` → "Open/allow the management port (PVE 8006 / HV 5985) from Core."

Everything else (`DNS_UNRESOLVED`, `MGMT_TRANSPORT_ERROR`, `SCAN_FAILED`, the HV `AUTH_REJECTED`) carries
`hint: null` until we have a confident remediation — we do **not** guess.

### Consequences / who picks this up

- **Code (engineering build slice):** add the types to `VMentory.Core`; change
  `IVirtualizationProvider` return shapes; split PVE 401/403 in `ProxmoxProvider`; classify WinRM
  substrings in `HyperVProvider`/`Reachability`; add the single evaluator and route both Poller and
  add-host through it; emit `health` in `/api/state` + SSE. Sits naturally **before or with the
  multi-platform dashboard rebuild** (it is that rebuild's data dependency) and dovetails with slice (4)
  Proxmox read (the PVE 401/403 split is Proxmox-provider work).
- **Docs agent:** fold the `HostHealth`/`HostFault` model + the `health` wire contract into the relevant
  spec(s) under `docs/phase2/specs/` and reference **ENG-0011a**. Do not restate the taxonomy in two
  places — Core is the source of truth.
- **Design agent:** dashboard rebuild is **unblocked** — code against the `health` JSON in §5; own the
  six-tier visual language + fault drill-down + the informational ICMP badge.

### Flagged build-time sub-questions (NOT blocking ENG-0011a)

- **Slice-5 Proxmox SSH user (single root vs per-node)** interaction with a future **transport-stage
  fault.** When the constrained SSH transport (ENG-0009, slice 5) lands, an SSH connect/auth failure is a
  *second* transport stage distinct from the REST mgmt auth. Whether SSH gets its own `FailureStage` /
  codes (e.g. `SSH_TRANSPORT_ERROR`, `SSH_AUTH_REJECTED`) and how the single-root-vs-per-node user choice
  (open in ENG-0012) affects that classification is deferred to the slice-5 build. The `FailureStage`
  enum is designed to grow.
- **Exact cross-path transition handling while the old booleans are removed.** The precise sequencing of
  keeping the derived legacy `Reachability` booleans / `AddError` / `ScanError` on the wire until the UI
  cuts over to `health.*`, then deleting them, is a build-time mechanics question, not a design decision.

### Boundary restated

Everything above is **only** ENG-0011a. The broader ENG-0011 observability contract — logging substrate
(Fork 1), persisted sinks + retention coordinated with `persistence-and-security.md` §7 (Fork 2/6),
audit-vs-ops-log separation (Fork 3), the uniform per-transport diagnostic envelope (Fork 5), and the env
config surface (Fork 8) — **remains Open / plan-only and is not decided by this record.**
