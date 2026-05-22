# Host Overview redesign — pick one direction

**Mockups:**
- `design/mockups/2026-05-22-overview-dense.html`
- `design/mockups/2026-05-22-overview-cards.html`
- `design/mockups/2026-05-22-overview-health-hero.html`

**Proposal:** `design/proposals/2026-05-22-overview-redesign.md` (read this first for context)
**Status:** Proposed — **awaiting human pick** before implementation
**Touches:** `wwwroot/index.html` (sections: `/* Host grid */`, `/* Host card */`, and the `renderHostCard` JS function — plus a new toolbar / KPI strip above the grid)

## What to change

Replace the current full-width single-column host card with one of three layouts. **The dev will tell you which.** Do not start implementation until that decision lands in `STATUS.md` (the row moves from *Proposed* to *In progress* and names the chosen direction).

Regardless of which direction is picked, the following sub-changes apply (they're orthogonal to the layout choice):

1. **Label the reachability dots.** Put the letters `P` / `T` / `W` inside the existing 20×20 circles (ping / TCP 5985 / WinRM). Keep the existing tooltip — letters supplement, don't replace.
2. **Surface uptime staleness.** When the host's last reboot is older than 90 days, render the boot value in `var(--yellow)`. Also switch the display from raw hours (`6095h ago`) to friendlier units: `<48h` → `Xh`, `<60d` → `Xd`, otherwise `Xd` still — but in yellow.
3. **Flag vCPU over-subscription.** When `sum(vm.vCPU) > host.cores`, render the vCPU stat value in `var(--yellow)` and append `/cores` (e.g. `40 /16`).
4. **Add a cluster KPI strip** above the host grid: Hosts reachable, Total VMs, Cluster vCPU vs cores, Cluster RAM allocated. Pull from the existing `totals` already in `/api/state`. (See cards and health-hero mockups for the visual.)

## Implementation notes

- Reuse the existing design tokens — do not introduce new colour variables. The mockups use only the tokens already in `:root` and `[data-theme="light"]`.
- Reuse the existing `.sbadge` classes for the scan-state pill — don't reinvent.
- Keep the SSE-driven re-render path: whatever you build, the diff-update on host change must still work. Don't switch to full-grid re-render.
- The `health` classification (ok / warn / bad) should live in JS as a derived field from the existing host data — don't add a backend field for it yet.
- Mockups have hand-rolled SVG ring / gauges. Reuse the existing canvas donut helper for the hero ring (don't introduce a separate inline-SVG approach in the live code).
- The card-grid mockup uses `grid-template-columns: repeat(auto-fill, minmax(420px, 1fr))` — match this for the live grid so single-column wrap still works on narrow viewports.

## Out of scope (do NOT also change)

- Sidebar nav and header bar
- Charts / Storage / VM Table pages
- Add-host modal, credentials modal, toasts
- Light theme polish (verify it doesn't break — but don't restyle it here)
- Backend API changes (no new fields, no new endpoints)
- Removing the existing serial number / CPU model detail from the card — keep these somewhere (footer is fine), don't drop the data

## Open questions to ask the dev before implementing

If the dev hasn't already answered these, drop a question into `requests/from-codebase/` rather than guessing:

1. Which direction (1 / 2 / 3 / hybrid) to implement?
2. Is real-time CPU/RAM **utilization** available, or only **allocation**? The gauges in direction 2 currently visualize allocation.
3. Is the 90-day uptime threshold for "stale" correct?
