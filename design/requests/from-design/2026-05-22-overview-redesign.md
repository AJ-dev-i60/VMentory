# Host Overview redesign — Option 1 (dense list)

**Mockup:** `design/mockups/2026-05-22-overview-dense.html`
**Proposal (rationale):** `design/proposals/2026-05-22-overview-redesign.md`
**Status:** Ready to implement — direction picked by dev on 2026-05-22
**Touches:** `wwwroot/index.html`
  - CSS: replace `/* Host grid */` and `/* Host card */` blocks
  - JS: replace `renderHostCard(host)` (and whatever calls it) with `renderHostRow(host)`
  - Markup: add a toolbar (filter chips + search) and a list-header row above the host list

## What the user sees after this lands

The Host Overview page becomes a table-style dense list instead of full-width cards. Open `design/mockups/2026-05-22-overview-dense.html` in a browser — that is the visual target. Match it within reason; do not introduce new colour tokens or new fonts.

### Key elements (all present in the mockup)

1. **Page header.** Title "Host Overview" + subline `N hosts · N VMs · cluster vCPU X / Y cores · last scan Tm ago`. "+ Add host" button on the right (reuses the existing add-host flow).
2. **Toolbar row.** Pill-style filter chips (`All` / `Healthy` / `Warning` / `Unreachable`) with counts, plus a search input on the right that filters by hostname, IP, or hardware model substring. Active chip uses `rgba(59,130,246,.12)` bg + `var(--blue)` border (see `.chip.active` in the mockup).
3. **List header row** (sticky-feeling top row in the list card): column labels in `text-transform:uppercase`, `var(--text3)`, ~10px.
4. **One row per host** (~52 px). Grid columns: `[health-dot] [Host] [Cores] [RAM] [VMs] [vCPU] [Hardware] [Uptime] [Scan] [Actions]`. Numeric cells use `font-variant-numeric:tabular-nums`.
5. **Action buttons** (rescan / remove) are hidden by default and fade in on row hover (`opacity` transition, ~120 ms).
6. **Responsive collapse** at `max-width: 1100px`: drop the `vCPU` and `Scan` columns (the data is still visible elsewhere — vCPU is implied by the VMs count and detail page; Scan is shown by health-dot colour).

## Cross-cutting changes (apply regardless of layout)

These were called out in the proposal and the dev hasn't pushed back on them — implement alongside the layout change.

1. **Label the reachability dots.** Put the letters `P` / `T` / `W` inside the existing 20×20 circles (ping / TCP 5985 / WinRM auth). Keep the existing tooltip — letters supplement, don't replace.
2. **Surface uptime staleness.** Switch the boot display from raw `Xh ago` to friendlier units:
   - `< 48 h` → `Xh`
   - `< 60 d` → `Xd`
   - `≥ 60 d` → `Xd` rendered in `var(--yellow)` with a tooltip "No reboot in N days"
   - The 90-day threshold I originally suggested is wrong if the dev wants a tighter signal — **ask in from-codebase/ if unsure**, but `60d → yellow` is a reasonable default until they answer.
3. **Flag vCPU over-subscription.** When `sum(vm.vCPU) > host.cores`, render the vCPU stat in `var(--yellow)` and show as `40 /16` (vCPU / cores). When not oversub, show plain `22 /24` in default text colour. The slash-and-denominator format is consistent in both cases.
4. **Derived `health` field in JS** (do NOT add a backend field): `bad` if any reachability check fails OR last scan errored; `warn` if vCPU oversub OR uptime ≥ 60d; otherwise `ok`. Drive the left-column dot colour and the future filter chips from this single derived value.
5. **Cluster KPI row above the list** — see the page-sub line in the mockup. For now this is a single text line. The richer KPI strip from the cards mockup is **deferred** (separate follow-up if the dev wants it).

## Implementation notes

- Reuse the existing design tokens only. The mockup uses no new colour variables.
- Reuse `.sbadge` for the scan-state pill — don't reinvent.
- Reuse the existing `os-icon` SVG rendering (Windows 4-square etc.) — the mockup uses inline SVG but the live code has a helper; keep using it.
- **SSE diff path must keep working.** When a single host changes, only that row should re-render. The grid is keyed by `host.id` already — preserve that.
- The filter chips, the search input, and the section grouping all run client-side over the existing host list — no API changes required.
- Light theme: the mockup inherits the `:root` / `[data-theme="light"]` tokens. Skim it in light mode after wiring up; if anything looks wrong, drop a from-codebase request rather than fixing it ad-hoc.

## Out of scope (do NOT also change)

- Sidebar nav and the existing top header bar
- Charts / Storage / VM Table / Cluster Stats pages
- Add-host modal, credentials modal, toasts
- Light theme polish (verify it still works — but don't restyle)
- Backend API changes (no new fields, no new endpoints)
- Removing serial number / CPU model — keep them accessible (the `Hardware` column abbreviates as `PowerEdge R820 · E5-4610`; full strings go in a tooltip on that cell)
- The richer cluster KPI strip from the cards mockup — that's a separate follow-up

## Open questions

If the answer matters to your implementation, drop the question in `design/requests/from-codebase/` and pause that part of the work. The dev hasn't answered these yet:

1. Real-time CPU/RAM **utilization** — available, or only allocation? Mockup shows allocation only, which is safe.
2. Correct uptime threshold for "stale" — defaulting to `60d → yellow` until told otherwise.
3. Typical host count — affects whether sort is necessary (mockup has no sort yet; can add column-header sort later).
