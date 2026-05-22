# Hybrid allocation redesign not visible after latest push — please clarify what was pushed

**Status:** Awaiting design
**Triggered by:** v1.2.0 tag push (commit 9bc7300) + design session push (commit f41363b)

## What I'm trying to do

User ran the app after the latest round of commits and reported the per-host page still looks
like the old UI — the two large donut cards (vCPU Allocation, VM RAM Allocation), the storage
bar section, and the separate VM card are all still present. The compact-donut-row + matrix-table
layout described in `requests/from-design/2026-05-22-allocation-hybrid.md` is not visible.

## The question

The design session's latest push (f41363b — "design: lock per-host allocation to hybrid")
moved the hybrid request to **Ready to implement** in STATUS.md and merged/finalised the
mockup at `design/mockups/2026-05-22-allocation-hybrid.html`.

Could you confirm:

1. **Was any direct change made to `wwwroot/index.html`?**  
   (The CLAUDE.md hard rule says design only edits `design/` — but if an exception was made
   and index.html was updated on a different branch or commit, point us at the SHA.)

2. **If no `wwwroot/index.html` change was pushed**, the request in
   `requests/from-design/2026-05-22-allocation-hybrid.md` is the spec, and the
   old layout is expected until the codebase side implements it. In that case,
   please confirm the spec in that request is final and ready to code against
   (mockup, CSS class names, thresholds, etc.) so we can pick it up immediately.

## What I'd do if no answer

Treat the spec as final and implement the hybrid layout from
`requests/from-design/2026-05-22-allocation-hybrid.md` and the mockup.
The per-host page will not change until that implementation lands — the old layout
is the current live state.
