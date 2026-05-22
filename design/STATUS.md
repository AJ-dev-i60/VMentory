# Design Status Board

Live state of design proposals. Newest at the top of each section.

## Proposed
*(none)*

## Ready to implement
| Date | Title | Mockup | Request | Notes |
|---|---|---|---|---|
| 2026-05-22 | Replace "Changes since last scan" panel with toast notice | *(behavior change, no mockup)* | `requests/from-design/2026-05-22-scan-changes-toast.md` | Drop the inline `.diff-panel`; enrich existing `toast('Scan complete', ...)` with a short counts summary. No open questions. |
| 2026-05-22 | Per-host stats page (replaces RAM/CPU/Storage tabs) | `mockups/2026-05-22-per-host-stats.html` | `requests/from-design/2026-05-22-per-host-stats.md` | Click hostname on overview → dedicated host page combining vCPU donut + RAM donut + Storage bars + filtered VM table. Sidebar drops RAM/CPU/Storage items. All 4 open questions resolved by dev (hash routing yes, whole-row click yes, Cluster Stats untouched, per-host re-scan API supported). Empty-state behavior specced inline. |
| 2026-05-22 | Value formatting (duration, GB/TB, disk usage) | mockup `2026-05-22-per-host-stats.html` reflects new formatting | `requests/from-design/2026-05-22-value-formatting.md` | Adds `fmtDuration`, `fmtGB`, `fmtDiskUse` helpers. Collapses VM Table's two Disk columns into one. Apply at every render site (Overview row, top-level VM Table, per-host page). Pure frontend. |
| 2026-05-22 | VM guest OS — backend collect + leftmost icon column | mockup `2026-05-22-per-host-stats.html` (VM card now has OS column) | `requests/from-design/2026-05-22-vm-guest-os.md` | Backend: add `Vm.GuestOs` and fetch via Hyper-V KVP in `Scanner.cs`. Frontend: tiny `vmOsIcon()` helper + new leftmost column on both VM-table render sites. VMs without integration services get a neutral `?` fallback. Dev approved 2026-05-22. |

## In progress
*(none)*

## Awaiting design
*(none)*

## Shipped
| Date | Title | Commit | Notes |
|---|---|---|---|
| 2026-05-22 | Host overview — dense list (Option 1) | 7386648 | Dense list with health dots, filter chips, search, P/T/W reach labels, uptime staleness (60d→yellow), vCPU oversubscription flag, OS icons, responsive collapse at 1100px. Open questions (utilisation data, uptime threshold, sort) deferred — used 60d default. |
