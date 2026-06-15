# Multi-platform dashboard — two directions

**Mockups:**
- `mockups/2026-06-15-dashboard-unified-list.html` — Direction A: one list, platform as a column
- `mockups/2026-06-15-dashboard-grouped.html` — Direction B: platform-grouped sections

## The problem

Phase 1 is Hyper-V only. Phase 2 (ROADMAP 2.1) puts Proxmox hosts/VMs in the same dashboard. The design tension: make two platforms feel like *one* product without **hiding the differences that matter** — Proxmox is clustered, exposes live CPU util, and can't (yet) be a migration *source*; Hyper-V is reached via an on-host agent, has no live host CPU%, and is the migration source. The unification must be honest.

## Shared design decisions (apply to both directions)

- **New platform accent tokens.** Proposed additions to the `:root` block: `--hv:#0ea5e9` (azure) and `--pve:#e67817` (Proxmox brand orange). These extend the palette rather than reusing `--blue`/`--purple`, so platform identity is unambiguous and never collides with the existing blue=primary / green=ok / red=bad semantics. Both also get a light-theme-safe contrast (they're saturated enough to read on `--bg-card` in both themes).
- **Platform badge** (`.pbadge.hv` / `.pbadge.pve`) — a small icon+label pill reusing the `.vmst`/`.sbadge` pill idiom already in the app. Hyper-V = rectangle glyph, Proxmox = cube/box glyph.
- **Capability-aware rendering.** Providers advertise `ProviderCapabilities` (ARCHITECTURE §2). The UI reads them in two places: (1) the Capacity cell renders whatever metrics the provider exposes — PVE shows a live **CPU%** row, HV shows RAM+Disk **allocation** only; (2) verbs are **gated** — the "Migrate →" action is live on HV rows and disabled with a reason tooltip on PVE rows. No button that 500s.
- **Cluster awareness.** PVE nodes carry a cluster identity; both directions surface the cluster as a grouping without losing per-node addressability.

## Direction A — single unified list

Keeps the live app's `.hrow` grid and adds **one Platform column** with the badge. Filter chips gain platform filters (`Hyper-V 4`, `Proxmox 3`) using the accent tokens. Everything stays in one sortable/filterable list.

**Wins:** one mental model, global sort/filter across *all* hosts regardless of platform ("show me the hottest host anywhere"), minimal new layout, closest to today's shipped overview. Cheapest to implement on top of `d986660`/`7386648`.

**Loses:** platform-level differences (transport, capability set) aren't visible at a glance — they're encoded per-row (badge, disabled verbs) rather than announced. Cluster grouping needs a tag/column rather than a natural home.

## Direction B — platform-grouped sections

Each platform gets a banded section: accent stripe, icon, **transport line** ("via VMentory Agent" / "via REST + SSH"), and a **per-platform capability legend** (green chips for supported, struck-through for not). Nodes list under their section; the PVE cluster tag lives in the section header.

**Wins:** the *differences* are the headline — an operator instantly sees Proxmox can't be a migration source and Hyper-V has no live CPU%. Natural home for cluster grouping and transport/health-of-connection info. Scales cleanly if a third platform (VMware, etc.) ever lands.

**Loses:** no single global sort/filter across platforms; more vertical space; cross-platform "which host is hottest anywhere" needs scanning two tables.

## My recommendation

**Ship Direction A as the default overview, borrow B's capability legend as a detail surface.**

A single list is the right primary view for an operator tool: the most common question is "what's the state of my fleet" and that wants one sortable table, not two. A is also the smallest delta on what's already shipped, which fits the ROADMAP's "no regression, de-risk early" stance for 2.1.

B's strongest idea — the explicit **per-platform capability legend** — shouldn't be lost. Fold it into a collapsible "platform connections" panel (or the provider's row in an Add-host / Settings view) so the capability/transport story is available without forcing the whole dashboard into bands. If a third platform ever arrives and the single list gets noisy, promote B's grouping to an opt-in "Group by platform" toggle on the same list — A and B share the exact same `.hrow` markup, so the toggle is layout-only.

## Out of scope

- VM-level table (the existing VM Table view) — same badge/capability treatment applies but isn't mocked here.
- Live time-series / historical charts (ROADMAP 2.1 "historical stats") — separate proposal.
- Management-verb affordances and destructive-op confirmations (ROADMAP 2.2) — separate proposal; this pass only shows the *migrate* verb gating as the capability example.
- Exact PVE cluster/HA visualisation beyond the cluster tag.
