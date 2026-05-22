# Host Overview — exploratory redesign

**Mockups:**
- `mockups/2026-05-22-overview-dense.html` — dense table-style rows
- `mockups/2026-05-22-overview-cards.html` — 2-up card grid with gauges + cluster KPI strip
- `mockups/2026-05-22-overview-health-hero.html` — cluster ring + health-grouped sections

## Why look at this now

The current full-width single-column card uses a lot of vertical space per host without giving a strong read on **cluster health**. Once you have more than ~6 hosts the user has to scroll past mostly-identical cards to find the one with an issue. A few other things stood out from the screenshot:

- The three green check icons aren't labelled — opaque unless you already know they mean ping / TCP / WinRM.
- `BOOT 6095h ago` (~253 days) is not visually distinct from `BOOT 36h ago`. Long uptime is signal worth surfacing.
- vCPU over-subscription (`vCPU 40 / 16 cores` on Rhea) is buried in two separate stat cells. Whether the host is over-committed is the most useful single number on this page.
- No aggregate "is the cluster OK" answer above the fold.

## The three directions and what they're each good at

### 1. Dense list — best for many hosts
- One row per host, ~52 px tall. Header row labels each column.
- Status is a single coloured dot at the left (the row's "is this OK" answer in one glance).
- Filter chips ("All / Healthy / Warning / Unreachable") + a search field replace the implicit scroll.
- Per-host actions (rescan, remove) only appear on hover — keeps the row quiet.
- vCPU shown as `22/24`, `40/16` (the second one in yellow when oversub).
- **Trade-off:** less room for visualization. The "gauge" idea doesn't fit at this density.
- **Best fit if:** users have 10+ hosts and the main task is "find the one that's broken."

### 2. Card grid — best for small clusters with room to breathe
- 2 cards per row at typical desktop width, auto-wraps to 1 on narrow viewports.
- Each card has two thin **gauge bars** (vCPU allocated, VM RAM allocated) above the existing four-stat row — keeps the current stats but adds the over-subscription visual.
- Cluster KPI strip across the top (Hosts up, Total VMs, vCPU, RAM) gives the cluster-health answer without leaving the page.
- Reachability dots get letters inside (P / T / W) to disambiguate without a tooltip.
- Coloured left border encodes status (green / yellow / red).
- **Trade-off:** still uses ~140 px per host; ~10 hosts fills the screen.
- **Best fit if:** users typically have 4–8 hosts and want a quick visual scan, not a table.

### 3. Health hero — best when "did anything break" is the primary question
- Hero panel up top: a single donut ring showing 4 / 5 hosts up, plus 4 cluster KPIs.
- Hosts are **grouped by health** into sections: Healthy, Warning, Unreachable. Each section auto-collapses if empty.
- Within each section, hosts are compact rows similar to direction 1 — but with a per-host vCPU bar on the right.
- The "Warning" subtitle inline-explains the warning (e.g. *"↑ 254d · 40 vCPU on 16 cores"*) so the user doesn't have to inspect to find out why.
- **Trade-off:** Less hierarchical when the cluster is entirely healthy (you get one big section with all of them). Most ops time.
- **Best fit if:** the user's daily check is "is anything wrong" rather than "show me the inventory."

## My take (non-binding)

If I had to pick one, **direction 3 (health hero)** does the most work for the user per pixel — and it composes well: when everything is healthy the page collapses into a single ring + one list, which is the right default. Direction 2 is the closest evolution of the current design and the lowest-risk path. Direction 1 is the right answer if you're scaling to 20+ hosts.

Easy remix: ship the **cluster KPI strip** (or the donut ring) from direction 3 *above* whatever card/list layout the dev prefers — it's mostly orthogonal to the host-row design.

## Open questions for the dev

- How many hosts do real users typically have? That decides density.
- Does VMentory know real-time CPU/RAM **utilization**, or only **allocation**? The gauges in direction 2 currently show allocation; if util is available, that's a better signal.
- Is "uptime > X days" actually a warning, or just informational? The mockups assume warning at 90d but that's a guess.
- Should the OS chip include build number / patch level somewhere?

## Out of scope for this first pass

- Sidebar nav restyling
- Charts page (RAM / CPU / Storage tabs)
- Modals (add host, credentials)
- Light theme — the mockups inherit the same tokens so they'll work, but I haven't checked.
