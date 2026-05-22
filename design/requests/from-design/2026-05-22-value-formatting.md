# Value formatting pass — durations, storage, disk usage display

**Mockup:** updated `mockups/2026-05-22-per-host-stats.html` (see storage rows + VM table for new formatting)
**Status:** Ready to implement (frontend-only)
**Touches:** `wwwroot/index.html` — add two formatter helpers and call them from every existing render site.

## What the user sees after this lands

Numbers that are currently dumped as raw `.NET` `TimeSpan` strings or unbroken-GB strings get rendered in a more readable way, everywhere they appear (overview, per-host page once built, top-level VM table, storage bars).

### Examples

| Raw value | New display |
|---|---|
| `1.11:29:49.9380000` (TimeSpan, VM uptime) | `1d 11h` |
| `65.17:41:28.9270000` | `65d 17h` |
| `09:23:42.5580000` (under a day) | `9h 23m` |
| `00:42:11` | `42m` |
| `(empty)` / `null` | `—` |
| `255.9 GB` (host RAM) | `256 GB` |
| `127.9 GB` | `128 GB` |
| `31.3 GB` (VM RAM) | `31.3 GB` (precision kept under 100 GB) |
| `1450.0 GB` | `1.42 TB` |
| `1833.0 GB` | `1.79 TB` |

### Disk usage (VM table + storage bars right-column value)

Currently the VM Table has two columns: **Disk (Thick)** and **Disk (Actual)**. **Collapse them into one column titled `Disk`**, formatted as:

```
<used> / <thick> [<used %>]
```

Both numbers in the same unit (chosen from whichever of used/thick is larger). Examples:

| Used | Thick | Display |
|---|---|---|
| 799.5 GB | 1450.0 GB | `0.78 / 1.42 TB [55%]` |
| 82.5 GB | 256.0 GB | `82.5 / 256 GB [32%]` |
| 1185.1 GB | 1650.0 GB | `1.16 / 1.61 TB [72%]` |
| 49.8 GB | 127.0 GB | `49.8 / 127 GB [39%]` |
| 0 | 127.0 GB | `0 / 127 GB [0%]` |

Storage bars (on the per-host page once built) use this same display string on the right side instead of the current `799.5 / 1450.0 GB`.

## Formatter spec

Add two pure helpers near the top of the JS block. Reuse everywhere a duration or GB/TB value is rendered.

### `fmtDuration(input)` — accepts a TimeSpan string or a number of minutes

```
input falsy or "00:00:00"          →  "—"
< 1 hour                           →  "Xm"          e.g. "42m"
< 1 day                            →  "Xh Ym"       e.g. "9h 23m"
≥ 1 day                            →  "Xd Yh"       e.g. "65d 17h"
```

Parse the `.NET` `TimeSpan` format `D.HH:MM:SS[.fffffff]` — days are optional and separated by `.`. Minutes precision is fine; drop seconds entirely.

### `fmtGB(gb)` — accepts a number of GB

```
gb is null / NaN / 0          →  "—"
gb < 1                        →  "<n> MB"           (n = Math.round(gb * 1024))
gb < 100                      →  "<n.n> GB"         (1 decimal, e.g. "31.3 GB")
gb < 1024                     →  "<n,nnn> GB"       (rounded, locale thousands sep, e.g. "1,023 GB")
gb ≥ 1024                     →  "<n.nn> TB"        (2 decimals, gb / 1024, e.g. "1.42 TB")
```

Locale: use `toLocaleString('en-US')` for thousands separator to match the existing app's English-period-decimal convention. If the dev wants a different locale later they can change one call site.

### `fmtDiskUse(usedGb, thickGb)` — disk usage combo

```
both 0 / null                 →  "—"
larger of (used, thick) < 1 TB → "<used> / <thick> GB [<pct>%]"
larger ≥ 1 TB                 →  "<used/1024> / <thick/1024> TB [<pct>%]"
pct = thick > 0 ? round(used/thick * 100) : 0
```

Inside the formatted output the two numbers must use the **same unit** and matching decimal style. Examples in the table above.

## Where to call them

Apply to every existing render site, not just the per-host page:

- **Overview row** (already shipped): RAM column → `fmtGB`. Uptime column → already formatted reasonably, but switch to `fmtDuration` for consistency if its output covers the same range.
- **VM Table** (top-level page): Uptime → `fmtDuration`. **Collapse the two Disk columns into one** using `fmtDiskUse`. RAM → `fmtGB`.
- **Per-host stats page** (about to be built per `2026-05-22-per-host-stats.md`):
  - Quick-facts strip: Host RAM → `fmtGB`, Uptime → `fmtDuration`, VM RAM total → `fmtGB`.
  - Donut card subheaders: any GB value → `fmtGB`.
  - Storage bars right-column: per-VM value uses `fmtDiskUse`. Host total row uses `fmtDiskUse(totalActual, totalThick)`.
  - VM card table: same column changes as the top-level VM Table.

## Out of scope

- **Adding a guest-OS field to the `Vm` model** — that's a separate concern (see `2026-05-22-vm-guest-os.md` if/when the dev approves it).
- Backend formatting — keep raw values in the API response; formatting is purely a frontend concern.
- Changing what columns the VM Table sorts by — the collapsed Disk column should sort by `used` (descending). If sorting both used and thick separately is desired, add a tiny "▾" toggle later.
- Locale switching — see note on `toLocaleString` above.
