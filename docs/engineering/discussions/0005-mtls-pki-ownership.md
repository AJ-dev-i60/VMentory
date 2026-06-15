# ENG-0005 — mTLS PKI ownership (agent enrollment & trust)

**Status:** Decided
**Raised:** 2026-06-16 by engineering session (promoted to critical path by ENG-0003/0004)
**Affects:** `docs/phase2/specs/agent-protocol.md`, `docs/phase2/specs/persistence-and-security.md`,
ROADMAP 2.0 (agent enrollment)
**Related:** [ENG-0002](0002-secret-store.md) (CA key storage), [ENG-0003](0003-agent-install-model.md)
(enrollment token), [ENG-0004](0004-agent-runtime-lifecycle.md) (mTLS channel, self-update)

## Context

Agent enrollment (ENG-0003) and the gRPC mTLS channel (ENG-0004) both require a CA to sign agent
certificates and a trust root the agent pins. This was parked as "lower-priority" but ENG-0003/0004
put it on the critical path: no 2.0 agent-enrollment code can be written until it's settled.

## Decision

**2026-06-16 · Owner decision, engineering session.**

### Ownership — **private CA inside Core, with a seam for an external CA later**
- Core runs its **own private CA** as the v1 default — generates the CA at first boot, signs agent
  client certs at enrollment, presents its own server cert. Self-contained, air-gap-friendly, zero
  external dependency. (This is what agent fleets do in practice: Teleport, Tailscale, Kubernetes,
  Consul Connect, step-ca.)
- A **pluggable interface** allows fronting an **enterprise CA (AD CS)** later for shops that want
  corporate-rooted trust — **not built in v1**. Mirrors the ENG-0002 `ISecretStore` pattern
  (self-contained default + optional external provider).

### Structure — **root + intermediate**
- An offline-ish **root** signs a single **intermediate**; the intermediate does day-to-day signing.
- Agents pin the **root**; Core signs agent + server certs with the **intermediate**. This lets the
  signing key rotate **without a fleet-wide re-pin** of the root.

### Lifetime & revocation — **short-lived certs + auto-renewal**
- Agent certs are short-lived (days/weeks); the agent **auto-renews over its existing mTLS channel**
  before expiry — the same trusted channel ENG-0004 uses for self-update.
- **Revocation = stop renewing + an identity allow/deny list in Core's registry.** No CRL/OCSP
  server to run, distribute, or keep reachable. Air-gap-friendly and operationally light.

### Enrollment flow (bootstrap trust)
1. Operator generates a **short-lived, single-use enrollment token** in Core; pastes it into the
   installer / first-run (ENG-0003 manual install).
2. Agent connects, validates Core's server cert against an operator-shown **fingerprint/pin**
   (trust-on-first-use gated by the token).
3. Agent generates its keypair **locally** (private key never leaves the host), sends a **CSR** +
   token; Core verifies the token (stored hashed, single-use, short TTL), signs the client cert with
   the intermediate, returns the cert + the **root to pin**.
4. Steady state: mutual mTLS; auto-renew before expiry over the same channel.

### CA key custody
- The CA private key lives in the **`ISecretStore`** (ENG-0002), KEK-wrapped. As the highest-value
  secret (its compromise = fleet compromise), it is the natural case for ENG-0002's opt-in
  **operator-passphrase KEK mode** in deployments that want it.

### Rationale
- Reuses infrastructure already being built (the ENG-0004 mTLS channel doubles as the renewal
  transport) and needs **no revocation server** — the lightest correct design for a single operator.
- Private-CA default works everywhere including air-gapped; the external-CA seam avoids a dead-end
  without paying for AD CS integration now.
- Root+intermediate keeps rotation sane from day one at negligible setup cost.

### Consequences
- **Operator-facing dashboard TLS is a separate trust domain** from the agent PKI — not governed by
  this decision.
- Core gains a small **internal-CA component** (issue/sign/renew/list/deny) backed by `ISecretStore`.
  Buildable in 2.0 alongside enrollment.
- Closes the "mTLS PKI ownership" critical-path item in PROGRESS §4.
- **Docs to propagate (documentation agent):** `agent-protocol.md` (enrollment handshake, renewal,
  pinning), `persistence-and-security.md` (CA key custody, allow/deny list, no CRL/OCSP).

## Open sub-questions (non-blocking)
- Exact cert TTL + renewal-window defaults (e.g. 14-day cert, renew at 7 days) — spec detail.
- Whether the single-use enrollment token can be bound to an expected host identity to harden TOFU.
- Code-signing cert for agent packages (ENG-0004) — related custody question, distinct from this
  transport CA; decide who signs and where that key lives.
