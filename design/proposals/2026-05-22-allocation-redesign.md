# Allocation section — three alternative directions

**Mockups:**
- `mockups/2026-05-22-allocation-bars.html` — A. Bars all the way (storage idiom extended)
- `mockups/2026-05-22-allocation-matrix.html` — B. Resource matrix (single VM-centric table)
- `mockups/2026-05-22-allocation-compact.html` — C. Compact gauges + unified table

## What's on the table

The current per-host page (shipped as `d986660`) has three different visual languages for three different resources:

- **vCPU**: donut + commit-ratio badge + legend
- **RAM**: donut + % allocated + legend
- **Storage**: stacked horizontal bars + per-VM rows + right-column values

That works fine, but it means the eye has to re-learn how to read each section. These three options each pick a single visual language and use it consistently across all three resources.

## A. Bars all the way

Lift the storage section's idiom upward. Each resource gets:

- A title line with the totals (e.g. `vCPU Allocation — 68 / 32 cores · Commit 2.13×`).
- One full-width stacked horizontal bar, segmented by VM (same legend colours as today).
- A short legend strip below.

For vCPU oversubscription, the bar extends past a `100%` redline marker and the over-the-line portion is patterned — visually unambiguous that the host is over-committed.

Storage keeps its per-VM rows because the actual-vs-thick overlay is useful and there's no clean way to encode it as a single stacked bar.

**Best fit if:** you want a single, predictable shape for every resource and consistency matters more than peak information density.

**Loses:** the donut's "instant pie chart" recognition for VMs that are dominant contributors.

## B. Resource matrix

Reframe the page around VMs instead of resources.

- A tiny top strip with three "host pressure" mini-bars (vCPU pressure 2.13×, RAM 73%, Storage 34%) — a 40-px summary you can scan in half a second.
- The main content is a single dense table where each row is a VM, with inline bars next to the numbers for vCPU, RAM, and Disk. The bars are sized to the VM's share of host capacity (vCPU column) or to the disk's used % (disk column).
- Sortable by any column. Severity colour (green/yellow/red) on the inline bars when a VM is the largest contributor or eating most of the resource.

This is the densest option. It answers "which VM is using the most of X?" instantly without needing to mentally pair donut slices to legend rows.

**Best fit if:** you typically arrive at the page asking *which VM is the problem* rather than *how loaded is this host*.

**Loses:** the at-a-glance host-wide view (the top strip is small). The "Free / Unallocated" share is harder to read.

## C. Compact gauges + unified table

Smaller version of what already exists, but reorganised.

- Three compact widgets in a row at the top: a vCPU donut, a RAM donut, and a Storage donut (all smaller than today — roughly 100×100 px). Just the totals + commit/%, no legend.
- Below: a single unified allocation table that **replaces** the donut legends and the storage section both. Columns: VM · State · vCPU (+ share %) · RAM (+ share %) · Disk (`used / thick [%]`) · Uptime.

This is the conservative option — keeps what works, drops the duplicated legends, treats storage as a fourth allocation column instead of a separate visualisation.

**Best fit if:** you like the current page but want it tighter and want everything on one screen without scrolling.

**Loses:** the actual-vs-thick storage overlay (the unified table only shows the disk's `used / thick` text + a single bar in the inline cell — you don't see the thick/actual distinction visually).

## My take (non-binding)

**B** is the strongest if VM-centric is genuinely how you arrive at the page. **A** is the strongest if consistency across resources is the win. **C** is the safest evolution of what's shipped — least risky to implement.

Easy remix: **B's top "pressure strip"** can be bolted onto A or C as a header. The three options aren't mutually exclusive in their pieces — feel free to pick parts.

## Out of scope

- Storage's per-VM actual-vs-thick visualisation in options B/C (could revisit if it turns out to matter).
- Real-time utilisation (still allocation-only — VMentory doesn't sample live util).
- Cluster Stats page (still deferred per prior call).
- The other sections on the per-host page (header, facts strip, hardware line, VM card) — all stay as shipped. Each mockup includes a stripped-down header just for context.
