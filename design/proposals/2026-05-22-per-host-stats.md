# Per-host stats page — replacing RAM / CPU / Storage tabs

**Mockup:** `mockups/2026-05-22-per-host-stats.html`

## Why look at this now

The dev called out that splitting **RAM Charts / CPU Charts / Storage** into separate sidebar tabs doesn't match how someone actually uses the app. The current tabs each render a **grid of all hosts** for that single resource — so to investigate "what's going on with Orion_20", you click through three different tabs and visually pair up the same card in each.

Inverting that is more natural: **drill into one host, see everything about it on one page.**

## Proposal

### Navigation change
- Sidebar drops **RAM Charts**, **CPU Charts**, and **Storage**.
- Sidebar keeps: **Overview**, **VM Table**, **Cluster Stats**.
- Hostnames on the overview list become clickable links → per-host detail page.
- Per-host page is reached *only* by clicking from the overview (not surfaced in the sidebar). Browser back works. Hash route like `#host/<id>` keeps it SPA-friendly.

### Per-host page layout (top to bottom)

1. **Breadcrumb row** — `← Overview` link, then page title with OS icon + hostname + status dots.
2. **Quick-facts strip** — Cores · Host RAM · VMs · vCPU (with /cores) · Uptime · Last scan. Reuses the same `.sstat` design from the existing cluster strip.
3. **Hardware line** — single line: `PowerEdge R820 · Xeon E5-4610 @ 2.40 GHz · S/N 4QY7WX1 · 10.0.20.20`.
4. **Two donut cards side by side** — vCPU Allocation (with commit ratio in the corner, identical to the current CPU Charts card) and VM RAM Allocation (identical to the current RAM Charts card).
5. **Storage section** — per-VM bars showing thick provisioned vs actual on-disk + host total, identical to the current Storage card for this host.
6. **VMs on this host** — a copy of the VM Table component, **pre-filtered to this host** (so the Host column drops; everything else stays). Sortable, searchable.

### Why this works
- All three current per-host visualizations get reused **verbatim** — the codebase Claude doesn't have to redesign donuts or storage bars, just relocate and key them by host.
- The cluster-wide "compare all hosts side-by-side" view, which the current tabs sort of provide, is mostly covered by the **Overview dense list** already (the new vCPU column flags oversub at a glance). The richer side-by-side comparison goes away — but the dev is OK with that based on the brief.

### Trade-offs
- **You lose the cross-host visual comparison.** If you wanted to glance at "are any of my hosts running hot on RAM?", you previously got four donuts on one page. Now you'd compare numbers on the overview (less visual). If this turns out to matter, the answer is to add small inline gauges to the overview rows (the cards mockup from the first pass already explored this — easy to revive).
- **Cluster Stats is the new home for any aggregate view.** Whatever it currently shows stays as-is for this round.

## Open questions for the dev

1. **Hash routing OK?** Currently the SPA tracks `st.view` and `switchView()`. Adding `#host/<id>` is a small extension. Confirm before the codebase side rewires.
2. **Click target on the row** — clicking the hostname is the obvious affordance. Should the whole row also be clickable (with a hover-cursor), or only the name? I'd suggest the whole row, with the existing per-row hover actions (rescan, remove) stopping propagation.
3. **What does Cluster Stats currently show?** I haven't inspected that view. If it's already showing per-host comparisons, this proposal partly overlaps — worth deciding what stays there.

## Out of scope for this pass

- Cluster Stats restyle
- Adding inline gauges to overview rows (could be a follow-up if cross-host comparison matters)
- A "compare two hosts" view
- Light theme verification (mockups inherit existing tokens; spot-check at implementation time)
