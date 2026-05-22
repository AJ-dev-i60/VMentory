# Design Status Board

Live state of design proposals. Newest at the top of each section.

## Proposed
| Date | Title | Mockup | Request | Notes |
|---|---|---|---|---|
| 2026-05-22 | Per-host stats page (replaces RAM/CPU/Storage tabs) | `mockups/2026-05-22-per-host-stats.html` | `requests/from-design/2026-05-22-per-host-stats.md` | Click hostname on overview → dedicated host page combining vCPU donut + RAM donut + Storage bars + filtered VM table. Sidebar drops RAM/CPU/Storage items. 4 open questions (hash routing, row-click target, Cluster Stats overlap, per-host re-scan API). |

## Ready to implement
| Date | Title | Mockup | Request | Notes |
|---|---|---|---|---|
| 2026-05-22 | Replace "Changes since last scan" panel with toast notice | *(behavior change, no mockup)* | `requests/from-design/2026-05-22-scan-changes-toast.md` | Drop the inline `.diff-panel`; enrich existing `toast('Scan complete', ...)` with a short counts summary. No open questions. |

## In progress
*(none)*

## Awaiting design
*(none)*

## Shipped
| Date | Title | Commit | Notes |
|---|---|---|---|
| 2026-05-22 | Host overview — dense list (Option 1) | 7386648 | Dense list with health dots, filter chips, search, P/T/W reach labels, uptime staleness (60d→yellow), vCPU oversubscription flag, OS icons, responsive collapse at 1100px. Open questions (utilisation data, uptime threshold, sort) deferred — used 60d default. |
