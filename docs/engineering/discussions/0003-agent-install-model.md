# ENG-0003 — Agent install / onboarding model

**Status:** Decided
**Raised:** 2026-06-15 by engineering session
**Affects:** `docs/phase2/specs/agent-protocol.md`, ROADMAP 2.0/2.2, ENG-0004 (agent runtime)
**Related:** [ENG-0001](0001-hyperv-migration-transport.md) (agent is the transport),
[ENG-0002](0002-secret-store.md) (mTLS identity, no stored admin password)

## Context

ENG-0001 chose a Windows agent as the steady-state Hyper-V transport. Open question: does VMentory
Core (Linux container) **remote push-install** the agent given an admin credential, or is install
**manual / org-managed**?

Key tension: remote install requires a remote-exec channel into Windows (WinRM/SMB) — exactly the
fragile path ENG-0001 removed. Bootstrap transport ≠ steady-state transport, but making WinRM the
*mandatory* onboarding mechanism walks back ENG-0001's spirit. Also: remote install needs an admin
credential reaching the Linux container (security teams commonly forbid this), and it is local
admin — **not domain admin** — that an install actually requires.

## Decision

**2026-06-15 · Owner decision: manual / org-managed install only. No remote push-install from the
container — now or as a planned follow-on.**

- The operator installs the agent on each host via MSI / GPO / SCCM / Intune (Windows) or the
  platform package (Linux, as/when Linux host roles appear).
- VMentory provides the signed installer + a short-lived **enrollment token**; the agent presents it
  on first contact and Core issues the agent its **mTLS client identity**. VMentory **never receives
  an admin credential** and stores none.
- No WinRM/SMB anywhere in the product, including onboarding. The `migrate-vm` WinRM knowledge
  remains only as the behavioral reference spec (per ENG-0001), not as shipping code.

### Rationale
- Keeps admin credentials out of the Linux container entirely — strongest security posture.
- WinRM never becomes load-bearing; ENG-0001's elimination of the fragile path stays intact.
- Matches how enterprises actually deploy host agents (GPO/SCCM/Intune). The target shops already
  run this machinery.
- The risk/investment of a remote-install path (transient-admin handling, AV-flagged service
  creation, two onboarding surfaces) wasn't justified to save a one-time manual step.

### Consequences
- Per-host manual install step at onboarding — accepted.
- **Install must include enrollment**, not just binary drop: provision agent mTLS cert / pin Core
  trust, then verify the agent answers. The enrollment-token + CA flow is owned by ENG-0004 / the
  mTLS-PKI sub-question in ENG-0002.
- **Update is separate from install** and does NOT reuse this manual path — once enrolled and
  trusted, the agent self-updates over its own mTLS channel (see ENG-0004).
- `agent-protocol.md` must specify the enrollment handshake (token → CSR → signed client cert).
