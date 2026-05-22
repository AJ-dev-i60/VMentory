# Replace inline "Changes since last scan" panel with a transient notice

**Mockup:** *(no mockup — behavior change only)*
**Status:** Ready to implement
**Touches:** `wwwroot/index.html`
  - Remove the `.diff-panel` block and the `diffBanner()` function (currently lines ~181–186 CSS, ~1009–1020 JS, and the call site at ~line 502 in the overview render).
  - Enrich the existing `toast('Scan complete', 'success')` call at ~line 458.

## What the user sees

After a scan completes, the overview no longer pushes the host list down with a "Changes since last scan" block listing every added VM. Instead, the existing scan-complete toast (bottom-right) carries a short summary like:

```
✓ Scan complete · 18 VMs added
```

The toast auto-dismisses on the existing toast timeout (no behavior change there). The host list sits at the top of the page from the moment scan completes.

## Spec

1. **Delete** the `.diff-panel`, `.diff-ttl`, `.diff-item`, `.diff-item.add`, `.diff-item.rem`, `.diff-item.chg` CSS rules.
2. **Delete** the `diffBanner()` function and the line in the overview view that calls it (`const out = diffBanner();` → just start with an empty string or remove that line).
3. **Build the summary string** from `st.diff` in `EventHub`'s scan-complete handler (line ~458). Counts → human string. Examples (combine with `·` separator, drop empty parts):
   - `18 VMs added`
   - `2 VMs added · 1 removed`
   - `1 VM changed` (singular)
   - When all counts are zero: `Scan complete · no changes`
   - When non-zero: `Scan complete · <summary>`
4. Pass the resulting string to the existing `toast(...)` call. Keep the `'success'` type.
5. **Do NOT** invent a new "view changes" affordance. If the dev later wants the full list back, they'll ask — for now the count alone is enough.

## Out of scope

- Don't change the toast component, the toast container, or the auto-dismiss timing.
- Don't change `st.diff` itself or the `/api/state` response — keep the data flowing even though the panel is gone (other views may use it later).
- Don't add a "changes log" page.
