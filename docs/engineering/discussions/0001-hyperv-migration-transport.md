# ENG-0001 — Hyper-V migration transport for the 2.3 MVP

**Status:** Decided
**Raised:** 2026-06-15 by orchestrator (after reviewing the `migrate-vm` skill)
**Affects:** `docs/phase2/ROADMAP.md` (2.0, 2.2, 2.3), `docs/phase2/specs/agent-protocol.md`, `docs/phase2/specs/migration-job-model.md`, `migrate-vm/` skill
**Related:** ARCHITECTURE locked decision #1 (Hyper-V via Windows agent)

## Context

Locked decision #1 ([ARCHITECTURE.md](../../phase2/ARCHITECTURE.md)) says VMentory reaches Hyper-V
through a **Windows agent installed on the host** — chosen because Linux→WinRM auth from a container
is fragile. That's the right *long-term* transport.

But reviewing the `migrate-vm` skill changed the picture: the user **already has a working
Linux→Hyper-V transport**. `migrate-vm/scripts/winrun.py` runs Hyper-V PowerShell over WinRM/NTLM
from the Proxmox host, driven by a least-privilege service account that's a local admin on the HV
hosts (granted via GPO). It's been validated end-to-end (cold-migrated an Ubuntu 22.04 Gen2 guest,
2026-06-12). The access-chain pain is real but **already solved and documented** in
`migrate-vm/references/centralized-access.md` (the 401 gotcha list: NetBIOS-vs-FQDN, local-admin
requirement, GPO linked-vs-created, elevated gpupdate, etc.).

So the migration MVP (2.3) does **not** strictly need the Windows agent to exist first — the
existing winrun.py path could drive the Hyper-V side now. The question is whether to lean on it.

Note also a related correction this review surfaced (tracked here for context, but really a docs
fix): the proven conversion path is **`qm importdisk` on the Proxmox node** (reads VHDX off a CIFS
mount, converts VHDX→raw), **not virt-v2v**. virt-v2v is at most an optional enhancement for
automating Windows virtio injection. The ARCHITECTURE/migration docs currently over-index on
virt-v2v and should be corrected regardless of how ENG-0001 resolves.

## The question

How do we drive the Hyper-V side of migration in the 2.3 MVP, relative to the long-term Windows
agent — reuse the proven winrun.py path now, build the agent first, or support both behind the
provider interface?

## Options

### A — Reuse winrun.py now, migrate to the agent later
VMentory's `HyperVProvider` orchestrates the existing winrun.py + `hyperv-prep.ps1` +
`fix-guest-netplan.sh` for 2.3. The Windows agent replaces the transport in a later milestone,
behind the same provider interface.
- **Pros:** fastest to a working end-to-end migration; reuses a validated path and the WinRM
  access-chain the user already fought through; decouples "ship migration" from "build agent."
- **Cons:** carries the fragile NTLM/GPO dependency into the product (temporarily); two transports
  exist during the transition; winrun.py is a Python helper outside the .NET stack.

### B — Build the Windows agent first, then migration
Finish the agent in 2.0/2.2; migration uses it from day one. No winrun.py in the product.
- **Pros:** single clean transport; the fragile path never ships; aligns with decision #1 as-is.
- **Cons:** migration slips until the agent is built **and** the access/trust model is re-proven
  through it; throws away a working asset for the MVP; more upfront work before any migration value.

### C — Support both transports behind the provider
`HyperVProvider` is pluggable (winrun.py OR agent), operator chooses per host.
- **Pros:** maximum flexibility; smooth migration off winrun.py; handles hosts where installing an
  agent isn't allowed.
- **Cons:** most surface area to build and test; two auth models to secure and document; risk of
  the "temporary" path becoming permanent by default.

## Recommendation

_Deferred to the engineering session — this file is the brief for that discussion._ The orchestrator's
prior lean was **A** (ship migration value fast, reuse proven assets, agent as a clean follow-on),
but the sequencing is genuinely the owner's call and depends on how soon the agent work is wanted
and how much the NTLM/GPO dependency is acceptable to ship even temporarily.

## Open sub-questions
- If A: does winrun.py stay a Python sidecar, or get reimplemented as a .NET WinRM client inside
  `VMentory.Providers.HyperV`? (Affects packaging — Python in the Core container vs pure .NET.)
- How does VMentory's secret store replace the manual `/root/.winrmcreds` / `/root/.smbcreds` files
  the skill uses today, without regressing the "no password in chat/at-rest" property?
- Does the agent (long-term) also subsume the SMB/CIFS disk read, or does disk transfer stay a
  Proxmox-node concern (CIFS mount + `qm importdisk`) independent of the HV control transport?
- Where does this leave same-platform HV→HV migration (2.4), which the agent is better suited to?

## Decision

**2026-06-15 · Choice: B — build the Windows agent first; migration uses it from day one. No
winrun.py in the product; the transport is reimplemented natively in .NET inside
`VMentory.Providers.HyperV` / the agent.** (Owner decision, engineering session.)

### Rationale
- A single, clean transport that never ships the fragile NTLM/GPO path or a Python sidecar in a
  .NET product. Aligns with ARCHITECTURE locked decision #1 as written.
- The validated `winrun.py` path and `references/centralized-access.md` (the 401/NetBIOS-vs-FQDN/
  local-admin/GPO gotcha list) are **not discarded** — they become the **behavioral reference spec**
  for what the agent must implement and the access model it replaces. The hard-won knowledge is
  encoded into the agent design, not thrown away.
- Security win that tipped the call: with winrun.py, VMentory must store a Windows **domain
  service-account password**. With the agent, the agent runs locally under a service identity
  (gMSA / Local System) and VMentory authenticates to it over **mTLS** — so there is **no Windows
  domain password at rest in VMentory's secret store**, only a scoped, revocable client cert. B
  materially shrinks the secret-handling surface (feeds the dedicated secrets discussion, ENG-0002).

### Consequences
- **Migration (2.3) now hard-depends on the agent landing in 2.0/2.2.** There is no winrun.py
  fallback. ROADMAP must treat "agent can drive guest VM control + the migration step graph
  (export/copy disk, prep, hand-off to `qm importdisk` on the Proxmox node)" as the **gate** for
  starting 2.3, not a stretch goal. If the agent slips, migration slips — accepted.
- `sub-question: Python sidecar vs .NET client` is resolved → **reimplement in .NET** (the agent is
  .NET; no Python in the Core container).
- Disk transfer stays a **Proxmox-node concern** (CIFS mount + `qm importdisk`), independent of the
  HV control transport — the agent owns HV-side control/export, not the Proxmox-side import. (Confirms
  the third open sub-question.)
- HV→HV same-platform migration (2.4) is now naturally served by the same agent — no second
  transport to introduce later.
- **Docs to propagate (documentation agent):** `agent-protocol.md` (the agent must cover the
  migration control verbs the skill exercises), `migration-job-model.md` (Hyper-V side driven by the
  agent, not winrun.py), and `ROADMAP.md` (2.0/2.2 agent = gate for 2.3). The virt-v2v→`qm importdisk`
  correction is separate and still pending regardless.

---

## AMENDMENT (2026-06-16) — DECIDED · via ENG-0009 (operator-approved)

**Not a reversal — a scoping amendment.** ENG-0009 (Decided) drives **Proxmox** deep
actions via its **native REST API + SSH, with no agent on Proxmox nodes** (operator-approved 2026-06-16). That confirms the agent is
**only the Hyper-V transport** — which is exactly what ENG-0001 already decided. Nothing in this
decision's *content* reverses; what changes is **scope framing and sequencing**:

- **Scope:** "the agent" is the **Hyper-V-source transport only** (plus any future *in-guest*
  customization agent, a separate topic). It is **not** a both-platforms transport. The agent's
  security win (mTLS instead of a stored Windows domain password) **still holds** and is unaffected.
- **Sequencing (the real change):** the agent + its PKI are **no longer the first foundation slice**.
  The first re-baselined releases — containerized Core + login/RBAC + planted Observe + Proxmox
  management/Deploy — **do not touch the agent**. The agent is sequenced with the **HV→PVE migration**
  work (2.3-era), since migration is its only release-1 consumer. "Agent is the 2.3 gate" still holds;
  "agent is front-loaded as 2.0's primary build" is **dropped**.
- **No winrun.py** still holds; the skill remains the behavioral reference spec.

**2026-06-16 · Operator approved the ENG-0009 re-baseline; this amendment is now Decided/in-effect.**
The agent is scoped to the Hyper-V migration source and de-front-loaded off the 2.0 critical path, as
described above. No content reversal of the 2026-06-15 decision.
