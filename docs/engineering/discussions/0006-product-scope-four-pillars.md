# ENG-0006 — Product scope: four-pillar platform & sequencing

**Status:** Decided
**Raised:** 2026-06-16 by engineering session (owner vision alignment)
**Affects:** `docs/phase2/ARCHITECTURE.md`, `docs/phase2/ROADMAP.md`, `docs/phase2/PROGRESS.md`,
all specs, the design workspace
**Related:** ENG-0001..0005 (these are the shared foundation, not migration-only)

## Context

The Phase-2 docs framed VMentory as a multi-platform dashboard + management + (one-directional)
migration tool. The owner's actual vision is larger: **a platform with four product pillars, each of
standalone value, on a shared foundation.** This decision records that scope so nobody specs the
narrower version.

## Decision (2026-06-16 · owner)

**VMentory is a container-based, single-operator platform with Windows + Linux on-device agents,
delivering four pillars on one shared foundation:**

1. **Observe** — resource/load dashboard: VM distribution, host utilization, capacity planning.
   Crucially **feeds placement recommendations** into Migrate and Deploy (the dashboard isn't passive
   — "see utilization → decide where to move/deploy"). *(Exists as Phase 1; generalize.)*
2. **Migrate** — wizard-driven **bidirectional HV ↔ PVE**. Handles all scenarios by operator choice:
   Windows checkpoint resolution (prompt to pick the set state → merge checkpoints), live-migrate vs
   shutdown-and-move, etc.
3. **Deploy** — full VM provisioning: ISO library + golden-image/template repository, creation
   wizard, quick-deploy from image or install-from-ISO, target-hypervisor choice, **guest
   customization** (IP DHCP/static auto-config, post-deploy app install e.g. Atera with operator-
   supplied package).
4. **Backup/Restore** — backup + restore to **either** platform, reports, restore-testability, dedup,
   storage management, off-site replication.

### Sequencing — **Observe → Migrate → Deploy → Backup**
Each pillar ships standalone value before the next. Matches the origin story (dashboard is the root,
migration the dream, deploy & backup the "furthermore").

### Migration directionality — **bidirectional is the end state; HV→PVE first**
Ship the proven HV→PVE path first (the `migrate-vm` skill). **PVE→HV is a distinct later effort** —
it is *not* "the same wizard reversed": it needs reverse driver handling (strip virtio, inject
Hyper-V drivers), qcow2→VHDX, Gen2/UEFI handling.

### Backup approach — **buy-vs-build deferred**
Whether to **orchestrate Proxmox Backup Server / Veeam** (honors "orchestrate proven tools";
dedup/incremental/verify/replication for free) vs **build a native backup engine** is **deferred** —
decided when pillars 1–3 are further along. Flagged as the single most consequential pillar-4 call;
building dedup/replication from scratch is arguably bigger than the other three pillars combined.
*(Future ENG topic.)*

## Foundation: the five prior decisions serve all four pillars (not wasted)
- **Agent (ENG-0001/0003/0004)** = on-device executor for *all* host work — inventory, lifecycle,
  migration export, **VM creation, ISO/image attach, unattend injection, snapshot/backup, restore**.
  The "constrained verb executor" design holds; the verb **catalog grows** substantially.
- **Job engine generalizes** from "migration DAG" to a **general operations engine** — migrate,
  deploy, backup, restore are all persisted, resumable, dry-runnable jobs on the same machinery.
- **Secrets (ENG-0002) / PKI (ENG-0005)** scale directly (more secret types; same store/CA).

## New shared subsystems the vision requires (currently absent from the architecture)
1. **Storage / repository layer** — ISOs, golden images/templates, **and** backup repositories
   (+ dedup). Underpins Deploy and Backup. Placement undecided (Core volume vs dedicated storage host
   vs native platform storage). *Biggest new gap.*
2. **Guest-customization layer** — cloud-init / sysprep / unattend, network (DHCP/static), post-deploy
   app install. **Shared by Deploy and Migrate** (the skill's netplan fix is its embryo) — build once.
3. **Scheduling** — backups are recurring; the ops engine needs a scheduler, not only on-demand jobs.
4. **Data movement at scale** — backup/restore/replication move large data (dedup, incrementals,
   offsite-over-WAN); a systems axis migration alone didn't demand.

## Consequences
- **"Phase 2" as written = pillar 1 + half of pillar 2.** Reframe planning as **shared foundation +
  4 pillars** (a multi-year platform), not a 5-milestone phase.
- **Docs to propagate (documentation agent):** ARCHITECTURE (4-pillar topology + the new
  subsystems + generalized ops engine + bidirectional migration), ROADMAP (insert Deploy & Backup
  pillars and the foundation reframe), PROGRESS orientation. Plus the still-pending virt-v2v→qm
  reconciliation.
- **Open future ENG topics seeded:** backup buy-vs-build; storage/repository placement;
  guest-customization engine choice (cloud-init vs platform-native); PVE→HV reverse migration.

## Decision
> Decided 2026-06-16 as above (owner). Sequencing Observe→Migrate→Deploy→Backup; bidirectional
> migration end-state with HV→PVE first; backup buy-vs-build deferred.
