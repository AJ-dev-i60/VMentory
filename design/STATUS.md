# Design Status Board

Live state of design proposals. Newest at the top of each section.

## Proposed
| Date | Title | Mockup | Request | Notes |
|---|---|---|---|---|
| 2026-06-15 | Migration wizard (HV→Proxmox) — two directions | `mockups/2026-06-15-migration-wizard-stepper.html` (setup), `mockups/2026-06-15-migration-run-controlroom.html` (run) | `requests/from-design/2026-06-15-migration-wizard.md` | ROADMAP 2.3. Two phases of one flow, seam at "Run": **A** linear stepper for precheck + resource mapping (CPU/RAM/firmware/storage/network), **B** control-room job DAG for run/validate/cutover with first-class partial failure, per-step retry/skip/rollback, "source intact" rollback safety, live+persisted SSE logs. Recommendation: use **both** (A=setup, B=run); same step spine drawn twice. Proposal in `proposals/2026-06-15-migration-wizard.md`. Awaiting human review. |
| 2026-06-15 | Multi-platform dashboard — two directions | `mockups/2026-06-15-dashboard-unified-list.html` (A), `mockups/2026-06-15-dashboard-grouped.html` (B) | `requests/from-design/2026-06-15-multi-platform-dashboard.md` | ROADMAP 2.1. Unify Hyper-V + Proxmox. **A** single list with a Platform badge column + capability-aware Capacity cell + capability-gated verbs. **B** platform-grouped sections with per-platform capability legend. New accent tokens proposed: `--hv #0ea5e9`, `--pve #e67817`. Recommendation: **A** as default, borrow B's capability legend. Proposal in `proposals/2026-06-15-multi-platform-dashboard.md`. Awaiting human pick (A vs B). |
| 2026-05-22 | Per-host allocation section — three alternative directions | `mockups/2026-05-22-allocation-{bars,matrix,compact}.html` | *(none yet — pick a direction first)* | Three alternatives for how the vCPU / RAM / Storage allocation section is visualised on the per-host page. A: bars-all-the-way (extend storage idiom). B: resource matrix (single VM-centric dense table). C: compact donuts + unified allocation table. Proposal in `proposals/2026-05-22-allocation-redesign.md`. Awaiting human pick before request is written. |

## Ready to implement
*(none)*

## In progress
*(none)*

## Awaiting design
| Date | Title | Request | Notes |
|---|---|---|---|
| 2026-06-19 | Credential management surface + structured token entry | `requests/from-codebase/2026-06-19-credential-management.md` | ENG-0012. Trigger: vega14 onboarding — compound PVE token (`user@realm!tokenid=secret`) mis-entered as bare UUID; per-host creds bulky. Interim hint+validation already shipped in the live app. Design a dedicated credential-management screen (named/reusable creds), add-host "pick a credential", and structured PVE-token fields. Sequence before/with slice (5) (adds SSH key as a 2nd per-host secret). |
| 2026-06-18 | Login screen + change-password wall + logout button | `requests/from-codebase/2026-06-18-login-screen.md` | Slice (2) login+RBAC backend is live. Interim unstyled screens exist and are wired up — design needs to produce styled versions. High priority: enables retiring the token banner on the deployed dev instance. |

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
