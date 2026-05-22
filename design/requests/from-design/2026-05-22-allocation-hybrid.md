# Per-host allocation section — switch to hybrid (compact donuts + matrix table)

**Mockup:** `design/mockups/2026-05-22-allocation-hybrid.html`
**Proposal (rationale):** `design/proposals/2026-05-22-allocation-redesign.md` (Option D)
**Status:** Ready to implement
**Touches:** `wwwroot/index.html` only — the `vHost()` render function and its helpers. No backend changes.

## What the user sees after this lands

The middle of the per-host page (everything between the hardware line and the bottom of the page) is **replaced** with:

1. A row of **three compact donut cards** showing vCPU / VM RAM / Storage at host level — ~84×84 donut + side metadata. Same line, equal width.
2. A single **matrix table** of VMs on this host, where each numeric column has an inline mini-bar showing the VM's share of host capacity.

What goes away:
- The two large donut cards (vCPU Allocation, VM RAM Allocation) — replaced by the compact donut row.
- The Storage section with its per-VM thick/actual bars — the per-VM info moves into the matrix table's Disk column. The actual-vs-thick visual distinction is intentionally dropped (mentioned in the proposal).
- The separate "VMs on this host" section — merged into the new matrix table.

What stays unchanged on the per-host page:
- Breadcrumb, page header, quick-facts strip, hardware line — exactly as shipped.

Open `design/mockups/2026-05-22-allocation-hybrid.html` in a browser. That is the visual target.

## Compact donut row

Three cards in a CSS grid (`grid-template-columns: repeat(3, 1fr); gap: 12px`). Each card has:

- An 84 × 84 donut on the left (canvas, using the existing donut helper — pass a smaller size param if it already accepts one, otherwise add one).
- A right-side metadata stack: uppercase label (vCPU / VM RAM / Storage), one big number (the headline metric), one small subtitle.

### vCPU donut
- Donut fill: 100% of the ring in `var(--red)` when oversubscribed (commit > 1); otherwise green up to the allocated portion + grey for the remaining free cores.
- Centre: big number = total vCPU; small label = `of N` where N = host cores.
- Metadata: label `vCPU`; big = `2.13× commit` (or whatever the commit ratio rounds to); subtitle = `Oversubscribed` in red when commit > 1, else `N free cores` in default text.
- Commit ratio: `sum(vm.vCpuCount) / host.totalCores`.

### VM RAM donut
- Donut fill: green segment for `sum(vm.RamGb) / host.totalRamGb`; grey for the remainder.
- Centre: `<pct>%`, label `allocated`.
- Metadata: label `VM RAM`; big = `<allocated> / <total host> GB` using `fmtGB`; subtitle = `<free> GB unallocated` (host RAM minus VM RAM).

### Storage donut
- Donut fill: green segment for `sum(vm.totalActualGb) / sum(host.Volumes.totalGb)`; grey for the remainder.
- Centre: `<pct>%`, label `used`.
- Metadata: label `Storage`; big = `<actual sum> / <volumes total>` using `fmtGB`; subtitle = `thick provisioned` (a static descriptor of which metric the donut shows).

Use `Math.round` for the percent shown in the centre. Use the existing `fmtGB` from the value-formatting work.

## Matrix table

This **replaces** the current per-host VM table (`vHost`'s VM card). The visible columns are the same set as today after the value-formatting work landed:

| OS icon | Name | State | vCPU | RAM | Disk (used / thick) | NICs | Uptime |
|---|---|---|---|---|---|---|---|

What's new vs. the current shipped per-host VM table:

1. **`vCPU` cell** — instead of the bare integer, render a small two-line cell:
   - line 1 (right-aligned): the vCPU count + a faint `<share>%` suffix, where `share = vm.vCpuCount / host.totalCores * 100` (rounded).
   - line 2: a 5px tall inline bar at 100% cell width, filled to `share%`. Colour by threshold (see below).

2. **`RAM` cell** — same shape: value + faint `<share>%` + inline bar at `share%`. Share = `vm.RamGb / host.totalRamGb * 100`. For VMs in `Off` state with no RAM allocated, render `—` and skip the bar.

3. **`Disk` cell** — same shape: keep the existing `fmtDiskUse` string (`0.59 / 1.49 TB [40%]`), but add a small inline bar below it, filled to the disk's used-of-thick percent. Colour by the same thresholds.

4. **Bar colour thresholds** (applies to all three columns):
   - `< 25%` → `var(--green)`
   - `25 – 49%` → `var(--yellow)`
   - `≥ 50%` → `var(--red)`

5. **Header note + legend** — keep the existing "VMs · N on this host" header. Append a small `bar = share of host capacity` muted hint. Add a tiny legend on the right of the header row with the three colour swatches. Both visible in the mockup.

6. **Other columns** (OS icon, Name, State, NICs, Uptime) — unchanged from the current shipped state.

Everything else about the table (sortable headers if already wired, hover state, search box if present) **stays the same**.

## CSS structure

The mockup uses these classes — feel free to keep the names or adapt to existing conventions:

- `.donut-row` / `.dcrd` / `.donut` / `.donut-ctr` / `.donut-num` / `.donut-lbl` / `.dcrd-name` / `.dcrd-big` / `.dcrd-sub` (`.warn`/`.bad` modifiers).
- `.mtbl` / `.mtbl-hdr` / `.legend-mini` / `.sq`.
- `.rc` (resource cell) / `.rc-val` / `.rc-val .pct` / `.rc-bar` / `.rc-fill` (with `.green`/`.yellow`/`.red`).

All colours are existing tokens — no new variables.

## Responsive collapse

Below `max-width: 900px` (same breakpoint as the rest of the per-host page):
- Donut row collapses to `grid-template-columns: 1fr` — three donut cards stack.
- Matrix table keeps the same columns but the cell padding shrinks; if width is genuinely cramped the responsive collapse can hide the NICs column first.

## Backend

**No changes.** All values are derivable from the existing `/api/state` response:

- vCPU share per VM → `vm.VCpuCount / host.TotalCores`
- RAM share per VM → `vm.RamGb / host.TotalRamGb` (where `vm.RamGb` is the existing derivation from `StartupRamMb` / `AssignedRamMb`)
- Disk used % per VM → already computed inside `fmtDiskUse`
- Storage donut numerator → `sum(host.Vms.map(v => v.TotalActualGb))`
- Storage donut denominator → `sum(host.Volumes.map(v => v.TotalGb))`
- vCPU commit ratio → `sum(host.Vms.map(v => v.VCpuCount)) / host.TotalCores`

If any of these turn out to be more involved than expected (e.g. a derivation already lives in `Store.cs` and you'd rather expose a computed field for it), feel free to add it server-side — but it's not required.

## Out of scope

- Top-level VM Table page (the cross-host one in the sidebar) — unchanged. The matrix-table treatment here applies **only** to the per-host page.
- Overview list, Cluster Stats — unchanged.
- Per-VM actual-vs-thick storage visualisation — deliberately dropped per the proposal. If anyone misses it later we'll add a per-VM detail hover or a per-VM popup.
- Light theme — the mockup uses only existing tokens, so should resolve cleanly. Spot-check at implementation; drop a from-codebase question if anything breaks.
- Sortable bar columns — the inline bar shouldn't change sort behaviour. Numeric sort on the underlying value still works.
