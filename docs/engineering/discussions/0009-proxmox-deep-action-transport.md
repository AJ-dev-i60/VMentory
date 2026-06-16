# ENG-0009 — Proxmox deep-action transport (REST API vs on-node agent vs hybrid)

**Status:** Decided (2026-06-16) — **A: native PVE REST API primary + constrained SSH; no agent on Proxmox nodes**
**Raised:** 2026-06-16 by engineering session (operator re-baseline of the Phase-2 foundation)
**Decided:** 2026-06-16 by operator (owner)
**Affects:** `docs/phase2/ARCHITECTURE.md` (Topology, decision #1 framing), `docs/phase2/ROADMAP.md`
(slice ordering), `docs/phase2/specs/proxmox-integration.md`, `VMentory.Providers.Proxmox`, ENG-0001/0003/0004/0005 (agent foundation scope)
**Related:** ENG-0007 (Proxmox-first North Star), ENG-0001 (HV transport), the `migrate-vm` skill (validated ground truth)

## Context

ENG-0007 made VMentory **Proxmox-first**: Proxmox is the primary, full-management platform; Hyper-V
declines to a migration source kept alive by light management. The operator has now frozen feature
work to re-baseline the foundation around a corrected frame:

- VMentory is a **containerized** app; the container hosts **all the tools**.
- Accessed only via a **web UI** (login + RBAC); the end user installs **nothing locally** beyond
  standing up the container.
- Core performs **deep actions on Proxmox** — via **either** a built/installed agent **or** the
  Proxmox API/SSH — **whichever is best and most reliable.** This record resolves that fork.

The original agent + private-CA mTLS foundation (ENG-0001/0003/0004/0005) was designed when **both**
platforms were assumed agent-driven. The validated `migrate-vm` skill shows the Proxmox side was
**never** agent-driven in practice: it drives Proxmox entirely through native `qm`/`qemu-img`/`qm
importdisk` over **SSH** plus the node's own tooling, and the matching REST surface is documented in
[proxmox-integration.md §2](../../phase2/specs/proxmox-integration.md). Proxmox ships a first-class
HTTPS API (port 8006) and an SSH shell on every node — it is **not** a black box that needs an agent
to become reachable, unlike a domain Windows host.

## The question

How should Core drive **deep actions** on Proxmox — VM lifecycle, create/clone, config, storage ops,
migration triggers, backup, guest-customization hooks — given Proxmox-first + Hyper-V-as-source?

## What the PVE REST API can and cannot do (grounded)

From [proxmox-integration.md §2–§3](../../phase2/specs/proxmox-integration.md) and the skill:

**REST API covers (token auth, `Authorization: PVEAPIToken=…`, privilege-separable via ACL):**
- Inventory + topology: `/cluster/resources?type=vm`, `/nodes`, `/nodes/{n}/status`.
- Live + historical stats: `status/current`, **`rrddata`** (native time-series — free history HV can't give).
- Config read: `/nodes/{n}/qemu/{vmid}/config` (firmware, disks, NICs, cores/mem).
- Lifecycle: `status/{start|stop|shutdown|reset}`, `snapshot`.
- **Create VM shell:** `POST /nodes/{n}/qemu` (vmid, cores, memory, bios, machine, net0…).
- Post-import wiring: `PUT/POST /config` (attach disk, `boot`, `efidisk0`, NIC model).
- Storage introspection + free-space precheck: `/nodes/{n}/storage`, `/storage/{s}/content`.
- **Native same-platform migration:** `POST /nodes/{n}/qemu/{vmid}/migrate` (PVE↔PVE).
- **Long-op progress:** every write returns a **UPID**; poll `tasks/{upid}/status` → SSE.
- **Backup (when pillar 4 lands):** `vzdump` and Proxmox Backup Server have REST/CLI surfaces.

**SSH is unavoidable (or strongly preferred) for:**
- **`qm importdisk`** — the proven migration baseline (VHDX→raw + attach). [proxmox-integration.md §3](../../phase2/specs/proxmox-integration.md)
  is explicit: **no clean REST equivalent** for importing an external disk image into a storage and attaching it.
- **`qemu-img info`** — disk inspection on the node filesystem (verify virtual size, no backing file).
- **Reading the source VHDX onto the node** (CIFS mount + node-local bulk transfer; out-of-band, not over a control channel).
- **Optional `virt-v2v`** — CLI tool, runs on the node (or conversion host); not the baseline.
- **Guest-customization on the node filesystem** — the skill's match-by-MAC netplan fix mounts the
  guest root *on the PVE node* (activate guest VG, edit, detach). That is a node-shell operation by nature.

The boundary is therefore **API for orchestration + stats + lifecycle + provisioning + native
migration; SSH for disk import/conversion/inspection + on-node guest-root edits.** Both are
node-native, neither requires VMentory to install software on the node. ⚠ *Uncertainty to verify:*
the exact ACL paths a privilege-separated token needs per milestone, and whether any newer PVE
release exposes a disk-import REST endpoint that would shrink the SSH surface — flagged in
[proxmox-integration.md §5 / gotcha 7](../../phase2/specs/proxmox-integration.md).

## Options

### A — Native PVE REST API primary + SSH for the gaps
API is the control plane for everything it covers; a forced-command-restricted SSH key handles
`qm importdisk` / `qemu-img` / optional `virt-v2v` / on-node guest edits.
- **Pros (capability):** covers *every* deep action VMentory needs — the API does orchestration/
  lifecycle/provision/native-migration, SSH does the disk/guest-root residue. Together = full coverage,
  proven by the skill. **Zero install on the node.** **Best operator UX** for "install nothing":
  onboard a node = paste a scoped API token + an SSH key, done. **Security:** scoped, privilege-
  separated, **revocable** token (revoke in PVE UI) + a forced-command SSH key — both already the
  ENG-0002 *tokens-over-passwords* intent. **Reliability:** rides Proxmox's own supported, versioned
  API + the same SSH path the skill validated; nothing custom to crash on the node. **Fit:** matches
  ARCHITECTURE topology as already drawn (PVE row = "API token (scoped) + SSH for qm importdisk").
- **Cons (accepts):** **two channels** to build and secure (typed `HttpClient` + SSH executor) — but
  the spec already designs for exactly this. SSH carries broad node power; must be constrained
  (forced-command, dedicated account). API/SSH version drift is Proxmox's contract, not ours to pin.

### B — Build + install a VMentory agent on the PVE nodes (extend ENG-0001 to Linux/PVE)
Ship the NativeAOT agent (ENG-0004) as a systemd unit on each Proxmox node; Core drives deep actions
via the agent's constrained verb set over gRPC/mTLS.
- **Pros:** one uniform transport across both platforms; a constrained-verb executor instead of raw
  SSH; mTLS identity instead of an SSH key; verbs are versioned/audited at the agent.
- **Cons (accepts):** **directly violates the "user installs nothing" tenet** for the platform we
  care about most — every node now needs an agent installed, enrolled, PKI-managed, and self-updated.
  **Re-implements what Proxmox already gives for free** (a supported API + a shell); the agent would
  mostly *shell out to the same `qm`/`qemu-img` anyway*, so it adds a layer without adding capability.
  **More to break:** an agent process that can crash, need rollback, or drift in version — on a node
  that already exposes everything natively. **Worst operator UX** (per-node install + enrollment).
  **Wrong fit for Linux/PVE:** ENG-0003's rationale for an agent was *Linux→WinRM auth fragility into
  Windows*; that rationale **does not exist** for Proxmox, which answers HTTPS + SSH directly.

### C — Hybrid: API-first, thin agent only where API/SSH genuinely fall short
API for everything it covers; a minimal optional agent only for a capability SSH can't reach well.
- **Pros:** keeps an agent escape hatch if a future deep action proves un-SSH-able.
- **Cons (accepts):** **there is no such gap today** — the skill proves SSH + API already cover the
  full deep-action set including disk import and guest-root edits. So the agent is a solution to a
  problem we don't have; building it now is speculative. Carries B's install/enrollment burden the
  moment any node needs the "thin" agent, breaking the install-nothing tenet partially. Two
  transports to test and document for marginal benefit.

## Recommendation

**Option A — PVE REST API as the primary control plane, SSH (forced-command, dedicated key) for the
disk-import / conversion / on-node guest-edit residue. No agent on Proxmox nodes.**

This is the only option that satisfies all three operator tenets at once: **deep actions fully
covered** (API + SSH together do everything the validated skill does and everything pillars 1–3 need),
**install nothing on the node** (token + key, both revocable), and **best reliability/UX for a single
operator** (ride Proxmox's own supported surfaces rather than a custom process). It also **fits the
existing ARCHITECTURE topology and the Proxmox spec as already written** — this is less a new decision
than a confirmation that the spec's API+SSH design is the *primary* and *sufficient* path, not a
stopgap until an agent arrives.

**The trade-off A accepts:** Core maintains **two Proxmox channels** (typed REST client + a
constrained SSH executor) and depends on **SSH** for the disk/guest residue — a broad-privilege
channel that must be locked down (dedicated account, forced-command where feasible, key in
`ISecretStore`). We accept this because the alternative (an agent) re-implements Proxmox's native
surfaces, violates "install nothing," and adds a failure domain without adding capability. The SSH
surface is **bounded and well-understood** (it is exactly the skill's command set), and constraining
it is a spec detail, not a new subsystem.

## Knock-on: this does NOT need the agent on Proxmox; the agent's real role shrinks to Hyper-V

Because A drives Proxmox with no agent, the on-device agent + private-CA mTLS foundation
(ENG-0001/0003/0004/0005) is **only load-bearing for the Hyper-V migration source** (and any future
*Windows/Linux guest-customization that must run inside a guest* — distinct from on-node edits, which
are SSH). That has two consequences the re-baseline must record (see ENG-0010 and the amendments below):

1. **The agent/PKI foundation is demoted from "front-loaded 2.0 gate for everything" to "scoped to the
   Hyper-V source, sequenced with migration (2.3-era), not the platform's first foundation slice."**
   It remains *correct* for what it does (mTLS instead of a stored domain password — ENG-0001's
   security win still holds); it is simply **no longer the first thing built**, because the first
   releases (planted Observe + Proxmox management/Deploy) don't touch it.
2. **The private CA / enrollment / self-update machinery serves a fleet of ~5 Windows HV hosts that are
   being retired** — a much smaller, shorter-lived surface than "every node on both platforms." Its
   scope and urgency drop accordingly.

## Open sub-questions

- **SSH hardening shape on PVE:** dedicated account + `sudo` for the few root ops vs a root-only
  forced-command key. Spec detail for `proxmox-integration.md`; intersects the still-open **PVE node
  TLS trust model** (proxmox-integration.md §5 OQ1).
- **Guest-customization that must run *inside* a guest** (sysprep/cloud-init injection, post-deploy app
  install à la Atera) — is that always achievable from the *node* (mount-and-edit / cloud-init drive),
  or does any case need an in-guest agent? If the latter, that is a *guest* agent, not a *node* agent,
  and a separate future ENG topic — does **not** revive the PVE-node-agent option. Flag to verify.
- **Verify with a live PVE:** confirm no current REST disk-import endpoint exists that would shrink the
  SSH dependency (would simplify, not change, the recommendation).

## Decision

**2026-06-16 · Operator (owner) approved Option A.** Proxmox deep actions are driven by the **native
PVE REST API as the primary control plane** (orchestration, lifecycle, provisioning, stats, native
PVE↔PVE migration, task/UPID progress), with a **constrained SSH key** handling the disk-import /
conversion / on-node guest-edit residue (`qm importdisk`, `qemu-img`, optional `virt-v2v`, mount-and-edit
guest-root fixes). **No agent is installed on Proxmox nodes.**

### Consequences (in effect)
- Onboarding a Proxmox node = paste a **scoped, privilege-separated API token** + a **constrained
  (forced-command, dedicated-account) SSH key**, both held in `ISecretStore` (ENG-0002) and revocable.
- The on-device agent + private-CA mTLS foundation (ENG-0001/0003/0004/0005) is **only load-bearing for
  the Hyper-V migration source** and is **de-front-loaded off the 2.0 critical path** — sequenced with
  the HV→PVE migration slice (2.3-era). The amendments on those four records are Decided/in-effect as of
  2026-06-16.
- The open sub-questions below (SSH hardening shape, in-guest customization, live-PVE disk-import REST
  check) remain as **spec details** for the documentation agent / proxmox-integration.md — they refine
  but do not reopen this decision.
