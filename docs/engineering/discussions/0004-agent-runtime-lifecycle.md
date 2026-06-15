# ENG-0004 — Agent runtime, packaging, self-update & security model

**Status:** Decided
**Raised:** 2026-06-15 by engineering session (owner stated requirements: lightweight, updatable,
secure, robust)
**Affects:** `docs/phase2/specs/agent-protocol.md`, `docs/phase2/specs/migration-job-model.md`,
ROADMAP 2.0/2.2, `IVirtualizationProvider` capability model
**Related:** [ENG-0001](0001-hyperv-migration-transport.md) (agent is the transport),
[ENG-0002](0002-secret-store.md) (mTLS identity), [ENG-0003](0003-agent-install-model.md) (manual
install + enrollment)

## Context

ENG-0001/0003 commit to a manually-installed, self-updating Windows agent as the steady-state
Hyper-V transport, enrolled via mTLS. The owner set four requirements — **lightweight, updatable,
secure, robust** — which this topic turns into concrete runtime/packaging/protocol choices. The
same agent is expected to serve Windows (Hyper-V) and any future Linux host role ("install on
windows and linux as required").

## Decision

**2026-06-15 · Owner decision, engineering session.**

### Packaging / runtime — **NativeAOT single binary**
- .NET 8 **NativeAOT** native executable (~5–15 MB), **no .NET runtime on the host**, fast cold
  start, low idle memory. Runs as a **Windows Service** (and a systemd unit on Linux).
- Cross-platform from one codebase — same agent serves the Windows HV host and future Linux host
  roles. (This is a reason to keep the agent in .NET rather than a separate Go/Rust agent.)
- AOT constraints handled with **source-generated JSON / gRPC** (no runtime reflection).
- Fallback only if AOT friction proves blocking: .NET self-contained single-file (~60–80 MB).

### Control protocol — **gRPC over HTTP/2 + mTLS**
- Strongly-typed, **versioned** service contract; streaming-friendly for live migration progress;
  efficient; pairs naturally with capability negotiation and AOT source-gen.
- **mTLS both directions:** agent presents its enrolled client identity; agent pins Core's cert.
- Listener firewalled to Core's address.

### Security — **agent is a constrained verb executor, not a remote shell**
- The decisive property: the agent exposes only a **fixed, versioned verb set** (management +
  migration step graph) — **never arbitrary PowerShell / RCE**. This is the security upgrade over
  `winrun.py`, which ran arbitrary remote PowerShell.
- **Service identity: support both** — **gMSA preferred** where AD allows (no stored password,
  auto-rotated, scoped to Hyper-V management), **dedicated local service account as fallback** for
  standalone/non-domain hosts. Documented trade-off; gMSA is the recommended path.
- **Signed binaries** (Authenticode on Windows); every executed verb is **audit-logged back to
  Core's history store**.
- mTLS identity provisioned at enrollment (ENG-0003); no admin credential ever stored (ENG-0002).

### Self-update — **over the agent's own trusted mTLS channel, never WinRM**
- Install is manual (ENG-0003); **update is not** and does not reuse the install path. Once enrolled
  and trusted, the agent self-updates over its mTLS channel.
- Core hosts **signed agent packages**; on check-in the agent compares version, downloads,
  **verifies signature**, stages, **atomically swaps**, and restarts.
- A **watchdog/bootstrapper** component survives the main-agent restart and **rolls back on a failed
  post-update health check.**
- Prior art for this exact shape exists in Phase-1 [Updater.cs](Updater.cs) (apply-on-launch +
  background download) — reuse the pattern.

### Robustness
- Windows Service with **SCM auto-restart** on failure; watchdog guards the update path.
- **Idempotent / resumable verbs** so a migration step survives an agent restart mid-job (ties into
  `migration-job-model.md` step graph).
- **Heartbeat/health** endpoint consumed by Core's poller; survives host reboot via the service.
- **Versioned protocol + capability negotiation** so Core and agent may differ by a version during
  rollout — fits the existing capability model.

### Rationale
- NativeAOT + gRPC + constrained-verb design directly satisfy lightweight/secure; the mTLS
  self-update + watchdog rollback satisfy updatable/robust without ever reintroducing WinRM.
- Supporting both service identities avoids blocking non-domain/standalone hosts while keeping gMSA
  as the secure default.

### Consequences
- **2.0 foundation work:** stand up the agent project (NativeAOT), the gRPC contract (source-gen),
  the enrollment handshake (token → CSR → Core-signed client cert), and the self-update/watchdog.
  These are the **gate for 2.3 migration** (per ENG-0001).
- **mTLS PKI ownership** (parked in PROGRESS §4 / ENG-0002 sub-question) is now on the critical path:
  enrollment requires Core to act as (or front) a CA that signs agent CSRs. Resolve before 2.0
  agent enrollment is built.
- **Docs to propagate (documentation agent):** `agent-protocol.md` (gRPC verb set, versioning,
  enrollment, self-update, health), `migration-job-model.md` (idempotent/resumable agent verbs),
  ROADMAP (agent runtime + self-update as 2.0/2.2 gate).

## Open sub-questions (non-blocking)
- gRPC `.proto` verb surface for v1 (management verbs vs migration step-graph verbs) — spec detail
  for `agent-protocol.md`.
- Code-signing certificate ownership/process for agent packages (who signs, where the key lives —
  intersects ENG-0002).
- Linux agent host roles: which Linux hosts get an agent vs stay API/SSH-managed (Proxmox stays
  API+SSH per locked decisions; revisit if KVM/other Linux virtualization is added).
