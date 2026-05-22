# Design Status Board

Live state of design proposals. Newest at the top of each section.

## Proposed
| Date | Title | Mockup | Request | Notes |
|---|---|---|---|---|
| 2026-05-22 | Per-host allocation section — three alternative directions | `mockups/2026-05-22-allocation-{bars,matrix,compact}.html` | *(none yet — pick a direction first)* | Three alternatives for how the vCPU / RAM / Storage allocation section is visualised on the per-host page. A: bars-all-the-way (extend storage idiom). B: resource matrix (single VM-centric dense table). C: compact donuts + unified allocation table. Proposal in `proposals/2026-05-22-allocation-redesign.md`. Awaiting human pick before request is written. |

## Ready to implement
*(none)*

## In progress
*(none)*

## Awaiting design
*(none)*

## Shipped
| Date | Title | Commit | Notes |
|---|---|---|---|
| 2026-05-22 | Per-host allocation — hybrid (compact donuts + matrix table) | db53098 | Two large canvas donut cards + storage section + VM card replaced with: 3-donut SVG compact row (vCPU/RAM/Storage) + matrix table with per-VM inline share bars (green <25%, yellow 25–49%, red ≥50%). `miniDonut()` helper added; `drawHostCharts` canvas calls removed. |
| 2026-05-22 | FQDN truncation + font size + OS icon polish | 5a6eee7 | `hostLabel()` strips domain suffix everywhere (full FQDN kept as tooltip). Font-size 14→15px. Linux Tux icon (yellow circle, visible on both themes). Win2022/11 gets rounded-rect icon; Win2019/other keeps 4-square icon. |
| 2026-05-22 | Capacity column on host overview | 7a94f0c | RAM% and Disk% mini bars per host in overview table (`hostCapCell()`). warn/crit colour thresholds at 70%/90%. |
| 2026-05-22 | VM guest OS — backend collect + leftmost icon column | d986660 | `Vm.GuestOs` added; KVP query per-VM in `Scanner.cs` (wrapped try/catch, OSName→OSFullName fallback); `vmOsIcon()` helper + leftmost column on both VM tables. |
| 2026-05-22 | Per-host stats page (replaces RAM/CPU/Storage tabs) | d986660 | Hash routing `#host/<id>`; whole-row click on overview; breadcrumb, facts strip, hardware line, CPU donut, RAM donut, storage card, filtered VM table; sidebar drops RAM/CPU/Storage items; per-host re-scan via `hostId` body param. |
| 2026-05-22 | Value formatting (duration, GB/TB, disk usage) | d986660 | `fmtDuration`, `fmtGB`, `fmtDiskUse` helpers; VM table's two Disk columns collapsed into one; applied at every render site. |
| 2026-05-22 | Replace "Changes since last scan" panel with toast notice | d986660 | Dropped `.diff-panel` CSS + `diffBanner()` JS; enriched `scanComplete` SSE handler with counts summary toast. |
| 2026-05-22 | Host overview — dense list (Option 1) | 7386648 | Dense list with health dots, filter chips, search, P/T/W reach labels, uptime staleness (60d→yellow), vCPU oversubscription flag, OS icons, responsive collapse at 1100px. Open questions (utilisation data, uptime threshold, sort) deferred — used 60d default. |
