# ENG-0007 — Product strategy & release-1 scope: Proxmox-first, HV-as-managed-source, incremental shipping

**Status:** Decided
**Raised:** 2026-06-16 by engineering session (owner strategy alignment)
**Affects:** `docs/phase2/ROADMAP.md` (release definition + sequencing), `docs/phase2/ARCHITECTURE.md`
(provider capability model must support Hyper-V management verbs), `docs/phase2/PROGRESS.md`, the
design workspace (priority), ENG-0006
**Related:** ENG-0006 (four-pillar scope — this **refines and sequences** it, does **not** supersede
it), ENG-0001 (agent transport / 2.3 gate), ENG-0004 (constrained-verb agent), the `migrate-vm` skill

## Context

ENG-0006 locked the **what**: four pillars (Observe / Migrate / Deploy / Backup) of standalone value on
one shared foundation. It did **not** fix the **strategic why**, the **weighting** between pillars, or
the **release line** — and a register reader could reasonably infer "four equal pillars, ship in the
listed order" without understanding that this is, at heart, a **migration-off-Hyper-V** programme.

The missing strategic frame, captured faithfully:

- **What v1 is.** VMentory v1 is a read-only **Observe** tool the owner built as a sysadmin to get a
  capacity / resource / placement overview of a ~5-host, ~20-VM (fluctuating) Windows+Linux Hyper-V
  cabinet — to manage CPU/memory allocation and retire forgotten VMs. It ships and works today, but is
  **in-memory, single-use, ephemeral** (`Store.cs` is a `ConcurrentDictionary`; `Program.cs`'s
  `/api/quit` purges session data on shutdown).
- **The North Star.** The organization intends to move **entirely off Hyper-V onto Proxmox.** That makes
  VMentory v2 a **Proxmox-first management super-tool**, not a neutral two-platform peer product.
  Hyper-V's role **declines over time** — its primary job in v2 is to be the thing migrated **out of**.
- **The two thrusts of v2.** (1) "**Plant**" v1's Observe features so they become **persistent +
  multi-platform** — this is *foundation work*, not a new pillar. (2) Make the app **manage the Proxmox
  side**, with Hyper-V as a **lesser, transitional** concern.

This decision records that frame and the release sequencing it implies, so no one specs a symmetric
"two equal platforms forever" product or treats the pillars as equally weighted.

## The question

Three forks, resolved together:
1. **Release cadence** — one big v2, or incremental usable releases per milestone?
2. **Hyper-V scope in v2** — source-only (migrate-out, no management), full parity with Proxmox, or
   something in between?
3. **Pillar weighting / first-release line** — given finite effort and a retire-Hyper-V goal, what
   ships in release 1 and what is explicitly deferred?

## Options

### Cadence — A: one consolidated v2 vs B: incremental per-milestone releases
- **A (big-bang v2):** ship Observe + management + migration together. **Pros:** one coherent story.
  **Cons (accepts):** no value delivered until the whole thing lands; the working v1 Observe tool sits
  frozen for the entire build; high risk of a long no-feedback stretch.
- **B (incremental):** each ROADMAP milestone (2.0 → 2.4) ships as its own usable release. **Pros:**
  continuous value; the planted dashboard is usable before management exists; de-risks early (matches
  the ROADMAP's stated "de-risk early, independently shippable" intent, `ROADMAP.md:9-10`). **Cons
  (accepts):** more release overhead; intermediate releases expose an unfinished platform.

### Hyper-V scope — A: source-only vs B: light management vs C: full parity
- **A (source-only):** Hyper-V gets Observe + migrate-out, no lifecycle verbs. **Pros:** zero effort
  spent on a platform being retired. **Cons (accepts):** during a multi-month/year transition the
  operator still **runs** the Hyper-V cabinet daily; forcing them back to Hyper-V Manager for every
  start/stop/reconfigure undercuts the "super-tool" value precisely while HV is still load-bearing.
- **B (light management):** beyond Observe, Hyper-V gains **basic management verbs** (start / stop /
  reconfigure) so it stays operable from VMentory through the transition. **Pros:** the operator manages
  the whole estate from one tool while HV winds down; reuses the same provider/agent/ops machinery being
  built for Proxmox anyway. **Cons (accepts):** **invests some effort in a platform we intend to
  retire** — the one real tension in this decision (addressed below).
- **C (full parity):** Hyper-V reaches feature parity with Proxmox (deploy, backup, advanced
  management). **Pros:** symmetric capability. **Cons (accepts):** large sustained investment in the
  platform we are explicitly leaving; directly contradicts the North Star; effort that should go to
  Proxmox and migration.

### Pillar weighting — refine ENG-0006's Observe→Migrate→Deploy→Backup
ENG-0006 sequenced **Observe → Migrate → Deploy → Backup**. Through the Proxmox-first lens, the
practical ordering of *effort* is **Observe (plant) → Proxmox management/Deploy → HV→PVE migration**:
the value is in getting onto Proxmox and operating it well, with migration as the bridge. This **refines**
ENG-0006's ordering (it does not reorder the pillars wholesale — Migrate remains pillar 2 conceptually);
it states which work is weighted first when effort is contended.

## Recommendation

**Cadence B + Hyper-V scope B + the refined weighting**, with an explicit release-1 cut line:

- **Incremental releases.** v2.0 = **planted Observe** (persistent, multi-platform dashboard); then
  Proxmox management; then HV→PVE migration; etc. Aligns 1:1 with the existing ROADMAP 2.0→2.4 line
  (`ROADMAP.md:25-81`).
- **Hyper-V = light management.** Not source-only, not full parity. This is the honest balance: spending
  a *little* on the retiring platform buys a coherent single-pane operator experience for the entire
  (possibly long) transition, and it largely **rides machinery built for Proxmox** — the
  `IVirtualizationProvider` abstraction, the constrained-verb agent (ENG-0004), and the operations
  engine all exist regardless. The marginal cost of exposing start/stop/reconfigure on the Hyper-V
  provider is small relative to standing those subsystems up. Full parity (C) would be the wasteful
  version; source-only (A) is falsely economical because it strands the operator on Hyper-V Manager
  while HV is still the production estate.
- **Build starts at 2.0 "plant v1":** rename `HyperInventory` → `VMentory.*`, split projects,
  SQLite/EF persistence, define `IVirtualizationProvider` + the capability model
  (`ROADMAP.md:28-37`).

**The trade-off this accepts:** we deliberately invest non-zero engineering effort into Hyper-V — a
platform we are committed to retiring. We accept that because the alternative (source-only) ignores that
Hyper-V remains the **live production estate** throughout the transition, and the management verbs are a
thin capability layer over subsystems we must build for Proxmox anyway. The discipline that keeps this
honest is **"light, not parity"**: Hyper-V gets only the verbs needed to keep it operable while it winds
down — no Deploy, no Backup, no advanced management investment.

### Architectural implication (record explicitly)
Light Hyper-V management means the **`IVirtualizationProvider` capability model must allow management
verbs on the Hyper-V provider, not only Proxmox.** If the capability model were designed as
"Hyper-V = read+export only, Proxmox = read+write," it would have to be reworked later. The 2.0
foundation work that defines `IVirtualizationProvider` + the capability model (`ROADMAP.md:28-30`) must
therefore treat **management verbs as capability-gated per provider**, with Hyper-V advertising at least
start/stop/reconfigure — not as a Proxmox-only surface. The documentation agent should fold this into
`ARCHITECTURE.md` (provider capability model) and the 2.0 foundation notes, citing ENG-0007.

## Release-1 deferrals (explicit)

Out of the **first** v2 release, deferred to deliver later:
- **PVE→HV reverse migration** — a nice-to-have, and already a **distinct later effort** per ENG-0006
  (`0006-product-scope-four-pillars.md:38-41`, `ROADMAP.md:74-79`); not "the wizard reversed."
- **Backup / Restore** — genuinely needed eventually, but **not in release 1**; consistent with
  ENG-0006's buy-vs-build deferral (`0006-product-scope-four-pillars.md:43-48`, `ROADMAP.md:99-106`).

## Relationship to ENG-0006

This **refines and sequences** ENG-0006; it does **not** supersede it. The four pillars stand as
defined. ENG-0007 adds: the strategic *why* (retire Hyper-V → Proxmox-first), the *weighting* (Observe
plant → Proxmox management/Deploy → HV→PVE migration), the *cadence* (incremental per-milestone), the
Hyper-V *scope* (light management), the *release-1 cut line*, and the provider-capability-model
implication. Where ENG-0006 said "Observe → Migrate → Deploy → Backup," that conceptual ordering holds;
ENG-0007 states which work is weighted first under contended effort and what is out of release 1.

## Open sub-questions

- **"Reconfigure" scope on Hyper-V.** "Light management = start/stop/reconfigure" — but *reconfigure*
  spans a range (vCPU/memory only vs disks/NICs/checkpoints). The minimal verb set that keeps HV
  "operable during transition" without drifting toward parity should be pinned when 2.2 management verbs
  are specced. *(Seed for a future ENG topic or a 2.2 spec note.)*
- **Transition-end signal.** When does Hyper-V light management get *removed* (last HV host migrated
  off)? Probably a product/ops call, not architectural — noted so it isn't forgotten.

## Decision
> Decided 2026-06-16 (owner). VMentory v2 is a **Proxmox-first management super-tool** whose strategic
> goal is to move the estate **off Hyper-V onto Proxmox**. **(1)** Release cadence is **incremental** —
> each ROADMAP milestone (2.0→2.4) ships as its own usable release; v2.0 = planted (persistent,
> multi-platform) Observe. **(2)** Hyper-V scope is **light management** (start/stop/reconfigure beyond
> Observe), not source-only and not full parity — **implication:** the `IVirtualizationProvider`
> capability model must support management verbs on the Hyper-V provider, recorded for the 2.0
> foundation + ARCHITECTURE. **(3)** Effort weighting (refining ENG-0006): **Observe (plant) → Proxmox
> management/Deploy → HV→PVE migration.** **(4)** Out of the first v2 release: **PVE→HV reverse
> migration** and **Backup/Restore** (deliver later). **(5)** Build starts at **2.0 "plant v1"**:
> rename HyperInventory→VMentory.*, split projects, SQLite/EF persistence, define `IVirtualizationProvider`
> + capability model. Refines and sequences ENG-0006; does not supersede it. The honest tension —
> investing any effort into a platform being retired — is accepted and bounded by "light, not parity."
