# Per-host stats page — replace RAM / CPU / Storage tabs

**Mockup:** `design/mockups/2026-05-22-per-host-stats.html`
**Proposal (rationale):** `design/proposals/2026-05-22-per-host-stats.md`
**Status:** Proposed — confirm direction before implementing (3 open questions in the proposal)
**Touches:** `wwwroot/index.html`
  - Sidebar nav: remove the `ram` / `cpu` / `storage` `.nav-item` entries (~lines 287–297).
  - Switch view: add a new view type `host` to `switchView()` / `renderView()` flow (~lines 480–490).
  - New render function: `vHost(hostId)` that composes the existing `vRamCharts`, `vCpuCharts`, `vStorage`, `vVms` outputs filtered to a single host (see "How to assemble" below).
  - Overview: make the hostname cell in `renderHostRow()` a clickable link → `switchView('host', host.id)`.
  - Routing: extend `st.view` to carry an optional `viewArg` (the host id) and update `location.hash` to `#host/<id>` when entering the per-host view.

## What the user sees

Clicking a hostname in the overview list opens a dedicated page for that host showing everything currently scattered across the RAM Charts / CPU Charts / Storage / VM Table tabs — but scoped to that one host. The sidebar tabs for RAM, CPU and Storage go away. The Overview, VM Table and Cluster Stats tabs stay.

Open `design/mockups/2026-05-22-per-host-stats.html` in a browser. That is the layout target.

### Page anatomy (top → bottom)

1. **Breadcrumb** — `← Overview / <hostname>`. Clicking `← Overview` calls `switchView('overview')`.
2. **Page header** — OS icon, hostname (20px bold) + OS + IP subline, reachability dots on the right, then `Re-scan` (this host only) and `Remove` action buttons.
3. **Quick-facts strip** — `.facts` row with: Cores · Host RAM · VMs · vCPU (with `/cores` denominator, yellow if over) · VM RAM total · Uptime · Last scan. Reuses the visual idiom of the existing top cluster strip but per-host.
4. **Hardware line** — single muted-text line: `CPU` / `Model` / `S/N` / `Boot` keyed values, comma-free, separated visually by spacing.
5. **Two donut cards side by side** (single column below `max-width:900px`):
   - **vCPU Allocation** — the existing CPU Chart card content, including the commit ratio badge in the header. Add a `Free` legend item (cores − sum(vCPU)) and a grey slice for it when commit < 1. When commit > 1 the "Free" slice goes away and the commit badge turns yellow (already does in current code).
   - **VM RAM Allocation** — the existing RAM Chart card content, with a `Free` slice = `hostRamGB − sum(vmRamGB)`.
6. **Storage card** — the existing Storage section but **only for this host**, with a `Total (host)` row pinned at the top.
7. **VMs card** — the existing VM Table component, **filtered to this host's VMs**. Drop the `Host` column (it's implied). Keep search, sort, state filter.

## How to assemble (implementation guidance)

Where possible, **call into the existing per-host renderers** instead of duplicating their markup:

- The current `vRamCharts()` / `vCpuCharts()` / `vStorage()` likely each loop over hosts and emit a section per host. Factor the inner per-host section into a helper (e.g. `ramCardFor(host)`, `cpuCardFor(host)`, `storageCardFor(host)`) and call those three from inside `vHost(hostId)`. The existing top-level views then just become `hosts.map(ramCardFor).join('')` etc. — same output as today, but reusable.
- The VM Table's row renderer can stay as-is — just call it with `vms.filter(v => v.hostId === hostId)`.

If the refactor is too invasive in one go, ship `vHost()` as a duplicate first and follow up with the refactor — but the duplicate path will go stale fast.

## Routing

- `st.view` becomes either a string (`'overview'`, `'vms'`, `'cluster'`) or an object `{ name: 'host', id: '...' }`. Choose whichever is less disruptive — a parallel `st.viewArg` field is also fine.
- On entering the host view, set `location.hash = '#host/' + id`. On `hashchange`, route back into `switchView()`. Reading the hash at boot lets browser back/forward and reload-on-host-page work.
- If the host id from the hash isn't in `st.hosts`, fall back to `overview` and show an info toast: `Host not found — returned to overview`.

## Cross-cutting changes

1. **Overview row click** — the existing `renderHostRow()` should wrap (at least) the host-name cell in a clickable element calling `switchView('host', host.id)`. The action buttons in the row (re-scan, remove) must `event.stopPropagation()`.
2. **Sidebar simplification** — remove the three nav items as listed above. Don't reshuffle the remaining ones.
3. **Re-scan button on the host page** — calls the existing scan endpoint scoped to this single host. If the API only supports "scan all", drop the per-host re-scan and label the button `Re-scan all` instead (or open a from-codebase question about it).

## Out of scope

- Cluster Stats restyle (still TBD what it currently shows).
- Adding inline gauges to the overview rows.
- "Compare two hosts" view.
- Removing or changing `st.diff` plumbing — the scan-changes-toast request handles its own concern.
- Light theme polish (verify the mockup tokens still resolve cleanly in light; raise from-codebase if not).

## Open questions to confirm with the dev (or via `requests/from-codebase/`)

1. **Hash routing** OK?
2. **Whole-row click** vs hostname-only click on the overview list?
3. **Cluster Stats** content — does it overlap with what this proposal does? If so, what stays?
4. **Per-host re-scan** — does the API support it, or only `Scan All`?
