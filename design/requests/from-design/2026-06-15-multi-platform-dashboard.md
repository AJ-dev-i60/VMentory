# Multi-platform dashboard — platform badges + capability-aware rendering

**Mockups:**
- `design/mockups/2026-06-15-dashboard-unified-list.html` — Direction A (recommended default)
- `design/mockups/2026-06-15-dashboard-grouped.html` — Direction B (capability legend to borrow)

**Proposal (rationale + recommendation):** `design/proposals/2026-06-15-multi-platform-dashboard.md`
**Status:** Proposed — awaiting human pick of A vs B before final wiring
**Touches:** `wwwroot/index.html`
- CSS `:root` / `[data-theme="light"]`: add two platform accent tokens
- CSS: extend `.hrow` grid + add `.pbadge` rules; extend `.chip` with platform variants
- JS: `renderHostRow(host)` — add platform badge cell + capability-driven Capacity cell + capability-gated action buttons; extend filter chips
- Markup: list-header row gains a Platform column

**Targets ROADMAP 2.1.** Do not start before the provider model + `ProviderCapabilities` exist (foundation 2.0). This is a UI spec for when Proxmox data is available.

## What the user sees after this lands

The overview list shows both Hyper-V and Proxmox hosts. Each row carries a **platform badge**. The Capacity cell shows whatever metrics that provider actually exposes. Verbs the provider can't do are disabled with a reason tooltip. Recommendation is **Direction A** (one list); see proposal for why and for how to fold in B's capability legend later.

## New design tokens (add to both `:root` and verify in light theme)

```css
--hv:#0ea5e9;   /* Hyper-V — azure */
--pve:#e67817;  /* Proxmox — brand orange */
```

These are *new* accents, intentionally not `--blue`/`--purple`, so platform identity never collides with primary/ok/warn/bad semantics. Both read on `--bg-card` in dark and light — verify in light mode and drop a from-codebase note if either is too low-contrast.

## Key elements (all in the Direction-A mockup)

1. **Platform badge** `.pbadge.hv` / `.pbadge.pve` — icon + label pill, same idiom as `.vmst`/`.sbadge`. HV = rectangle glyph; Proxmox = cube glyph (inline SVGs are in the mockup). Add a **Platform column** to the `.hrow` grid and the list-header.
2. **Platform filter chips** in the toolbar (`Hyper-V N`, `Proxmox N`) using `.chip.active.hv` / `.chip.active.pve` (accent-tinted). Client-side filter over the host list, same as the existing health chips.
3. **Capability-aware Capacity cell.** Reuse the existing `.cap-cell` / `.cap-row` mini-bars. Render rows from what the provider advertises:
   - Hyper-V: `RAM` + `Disk` **allocation** (today's behaviour — unchanged).
   - Proxmox: a live `CPU` row (PVE exposes real util via `status/current`/`rrddata`) in addition to RAM. Same widget, content driven by `host.platform` / capability flags — **do not hardcode by host name.**
4. **Capability-gated verbs.** The per-row "Migrate →" action button is **enabled** when the provider's capabilities include migrate-source (Hyper-V) and **disabled** (`act-btn:disabled`, greyed) with a `title` tooltip explaining why (Proxmox: "Migrate FROM Proxmox not supported"). General rule: never render a live verb the target can't honour.
5. **Cluster awareness.** PVE nodes show a cluster tag; surface the cluster in the page subline (Direction A) or, if Direction B is picked, in the section header.

## Implementation notes

- Reuse existing tokens + the two new accents only — no other new colours.
- Reuse `.cap-cell` mini-bar markup verbatim; only the *set of rows* changes per platform.
- Drive the badge, filter, capability cell, and verb gating from a single `host.platform` discriminator + `host.capabilities` (or equivalent the provider model exposes). The mockup hardcodes mock data inline — that's mock-only.
- **SSE diff path must keep working** — rows are keyed by `host.id`; a single host change re-renders only its row. Don't break this.
- Reuse the existing `os-icon` helper for guest/host OS glyphs.
- The grid `grid-template-columns` gains one ~92px Platform column; update the `@media(max-width:1100px)` collapse rule to keep the Platform column (it's high-value) and drop a numeric column instead.

## Out of scope (do NOT also change)

- VM Table view (same treatment applies later; not this pass).
- Live time-series / historical charts (separate 2.1 follow-up).
- Management-verb affordances + destructive-op confirmation dialogs (ROADMAP 2.2) — only the *migrate verb gating* is in scope as the capability example.
- Sidebar, header, add-host/credentials modals, toasts.
- Direction B's full grouped layout unless the human picks B — but **do** consider lifting B's per-platform **capability legend** into a small "platform connections" panel regardless (see proposal).

## Open questions

Drop in `requests/from-codebase/` if it blocks you:
1. Does the provider model expose per-host `capabilities` as flags the UI can read directly (preferred), or must the UI infer from `platform`? The capability-aware cell + verb gating assume readable flags.
2. For Proxmox, is live host CPU% available at list-render time, or only on the detail page? Mockup assumes it's cheap enough for the list. If not, fall back to RAM allocation only and add CPU on the detail page.
3. Cluster model: is a PVE cluster one registry entry with N nodes, or N independent host entries that share a cluster id? Affects whether grouping is structural or a derived tag.
