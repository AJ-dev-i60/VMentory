# ENG-0015 — Estate dashboard & remediation tracker

**Status:** Decided (2026-08-25) · **BUILT 2026-08-25** · **Owner:** human
**Relates to:** ENG-0007 (monitoring-first), ENG-0011a (health tiers), ENG-0013 (Hyper-V transport), ENG-0008 (RBAC)

## Trigger

Owner request, 2026-08-25, after the iSixty estate's Dell OpenManage console was brought back
under management and every iDRAC onboarded:

> I want a report for my environment — a web page that gives me a nice dash overview, with
> actionable items suggested, in a proper action-items list that I can tick off, add dependent
> items or maintenance schedules that will need to be scheduled, VMs on that machine that will be
> affected during this, etc. Perhaps this could become VMentory 2.0.

The action list already existed — as prose, on an Outline page (`07 · Action tracker`, 37 numbered
items with why/how/done-when, dependencies, site-visit and purchase batching). It was the estate's
only working document and it was drifting from the systems it described, because nothing read the
systems. The hardware monitor had meanwhile been detecting real faults for months with no one
looking. Two halves of one loop, neither closed.

## Decision

**Extend VMentory rather than start a new app.** The deployed dev instance already carried exactly
the substrate the request needed — container on the Coolify VPS, login + RBAC + OIDC, encrypted
secret store, SQLite with append-only snapshots, a working Proxmox provider, the host→VM domain
model, SSE — and its own roadmap slice 8 was "a multi-platform monitoring dashboard". What was
missing was a hardware source, an action domain, and a page. Building those elsewhere would have
meant rebuilding the rest.

Design points, each a call that could have gone another way:

1. **A `Machine` is the join point, and none of its three sources know about each other.** The
   hardware monitor keys by service tag; the hypervisor inventory keys by address; the tracker keys
   by machine slug. `MachineEntity` carries all three and the estate view joins at read time. This is
   what lets a bare-metal SQL box, a storage chassis with no iDRAC, an uncabled switch and a
   Proxmox host all sit on one page with honest gaps — "not monitored", "no guests recorded" — rather
   than being forced through the VM-shaped `IVirtualizationProvider` seam, which they do not fit.

2. **The hardware monitor is optional configuration, not a provider.** `VMENTORY_OME_URL/USER/
   PASSWORD` (+ `_SKIP_TLS`, `_INTERVAL`). Absent, the estate still renders — machines, inventory,
   actions — minus hardware health. OpenManage is what this estate has; the `HardwareSnapshot` shape
   (device → subsystems + faults) is generic enough that a Redfish-direct or iLO reader slots in
   beside it later.

3. **One source of truth for the action list: VMentory.** The Outline tracker was imported once as
   a seed (`wwwroot/estate-seed.json`, first run into empty tables only) and its item numbers were
   kept — `#28` means the same thing in a chat, on a visit checklist and in the UI, and new items
   continue the sequence. The Outline page stays as the narrative record with a banner pointing here;
   retiring it outright is the owner's call. Two live lists is the failure mode this estate keeps
   falling into, so the seed is deliberately not re-applied on redeploy.

4. **Priority is consequence; class is logistics; group is a label.** The Outline sections mixed
   the two ("🔴 data-loss this week" vs "🔵 needs a site visit"). Separating them is what makes the
   remediation report the owner asked for on 2026-08-03 fall out for free: filter `class ≠ Remote`
   and you have the visit; union the `purchases` and you have the shopping list.

5. **Suggested actions are raised once per fault and adopted, not duplicated.** `ActionSuggester`
   keys each raised item `ome:{serviceTag}:{fault.Key}`; a seeded item may carry the same key so the
   collector adopts it instead of raising a twin (items 18, 33, 34, 36 do). A fault that reappears
   after its item was ticked off **reopens** the item with a dated note — a replaced part that still
   flags is not done. A fault that stops appearing is *not* auto-closed: the operator ticks it, so
   the note trail says who decided it was fixed.

6. **Blast radius is computed, never stored.** For each affected machine: the live inventory when
   VMentory has scanned that host, else the recorded static list — and the UI says which, with the
   date it was verified. A static list is a claim about the past and is labelled as one. Today every
   Hyper-V host is static (ENG-0013 has not landed); vega14 goes live the moment it is registered.

7. **Dependencies are directed edges, cycle-rejected at the API.** Ticking off an item whose
   blockers are still open is *allowed* — the operator may know better — but the completion note
   records which items were still open. "Add a dependent item" is two buttons: a new prerequisite
   (blocks this) and a new follow-up (waits on this), each pre-wired on creation.

8. **Writes need `ConsolePermission.ManageActions`** (Admin, VmOperator). Delete is Admin-only and
   discouraged — `Dismissed` keeps the history. Reads need only a session.

9. **The deployed instance sits inside the client network.** The EdgeStudios Coolify VPS is VM 171
   on vega14, so it reads OpenManage at `172.0.0.200` directly. No tunnel, no relay, no snapshot
   push — live data was cheaper than the alternatives, which is why the design is poll-based.

## What was built

- `VMentory.Core/Estate/EstateModels.cs` — `HardwareSnapshot/Device/Fault`, `StaticVm`, the action
  enums, `ActionImpact`.
- `VMentory.Core/Persistence/EstateEntities.cs` + `AddEstate` migration — `Machines`, `Actions`,
  `ActionDependencies`, `ActionNotes`, `HardwareSnapshots`.
- `OmeClient.cs` — OME 3.10 REST reader; `EstateCollector.cs` — 5-minute poll, append-only
  snapshots, `ActionSuggester`; `EstateSeed.cs`; `EstateEndpoints.cs` — `/api/estate`,
  `/api/estate/refresh`, `/api/actions` CRUD, `/status`, `/notes`, `/deps`.
- `wwwroot/index.html` — Estate (tiles + machine cards), machine detail, Actions (filter chips,
  grouped by priority, expandable rows with why/how/done-when, blast radius, schedule, deps, notes),
  new/edit modal. Interim styling on the existing token set; design pass requested.

## Deliberately not in this slice

- **Hyper-V live inventory** — still gated on ENG-0013. The static lists carry the four hosts.
- **Backup posture as a live source** — the Arcserve daily report (`arcserve-report`) already
  produces a day-JSON contract; reading it is a small follow-on. The seed carries the five-machine
  coverage fact for now.
- **FortiGate / switch health** — mapped in Outline, not read here.
- **ENG-0011a health tiers** for the hypervisor hosts — the estate view shows the existing
  reachability; the tiers land with their own slice.
- **Multi-client** — single-operator, as everywhere. A second client is a second deployment.
- **Alerting from the dashboard** — OpenManage now emails; VMentory stays "health + inventory, no
  alerting" per ENG-0007.

## Verified live (2026-08-25)

Smoke-tested against the real OpenManage from a scratch database: 7 servers read, subsystem
roll-ups and faults derived (Atlas bay-5 predictive failure, Atlas/Sagan foreign disks and H710/H710P
battery roll-ups, Nextcloud PSU 1), seeded items adopted by key, blast radius for the Latte move
resolved to Rhea's and vega14's guests, dependency cycle rejected, status/note/schedule round-trips.
