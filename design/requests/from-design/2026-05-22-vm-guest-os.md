# VM guest OS — backend collection + leftmost icon column on VM table

**Mockup:** see updated `mockups/2026-05-22-per-host-stats.html` — VM card now has an OS icon column.
**Status:** Ready to implement (backend + frontend)
**Touches:**
  - `Models.cs` — add one field to the `Vm` class.
  - `Scanner.cs` — extend the per-VM data collection in the WinRM PowerShell script.
  - `wwwroot/index.html` — new leftmost column in both VM-table render sites + a tiny icon helper.

## What the user sees after this lands

Each row in the VM Table (and the per-host page's VM card) gets a small OS icon on the far left, matching the host-name icon style already used on the Overview list. VMs whose OS we can't determine (powered off, no integration services, KVP unavailable) get a neutral grey "?" placeholder so the column never feels empty.

## Backend

### `Models.cs`

Add one property to `Vm`:

```csharp
public string GuestOs { get; set; } = "";
```

That's it — store the raw KVP `OSName` (e.g. `Windows Server 2019 Standard`, `Ubuntu 22.04.3 LTS`, `Debian GNU/Linux 12`). Family/icon mapping happens on the frontend, where it's cheap to change.

### `Scanner.cs`

Extend the PowerShell scan that already runs per host so it harvests each VM's KVP `OSName` and attaches it to the existing VM data. Sketch (use `string.Format()` per CLAUDE.md gotcha #1):

```powershell
# Inside the existing Get-VM loop:
$vmCs = Get-WmiObject -Namespace root\virtualization\v2 -Class Msvm_ComputerSystem `
        -Filter "Name='$($vm.Id)'"
$kvp  = Get-WmiObject -Namespace root\virtualization\v2 -Query (
          "ASSOCIATORS OF {{Msvm_ComputerSystem.CreationClassName='Msvm_ComputerSystem'," +
          "Name='$($vm.Id)'}} WHERE ResultClass=Msvm_KvpExchangeComponent")
$osName = ($kvp.GuestIntrinsicExchangeItems |
           ForEach-Object { [xml]$_ } |
           Where-Object  { $_.INSTANCE.PROPERTY.VALUE -eq 'OSName' } |
           Select-Object -First 1).INSTANCE.PROPERTY |
           Where-Object  { $_.NAME -eq 'Data' } | Select-Object -ExpandProperty VALUE
# Include $osName in the per-VM JSON object the scan emits.
```

The exact incantation may need adjusting against whatever scheme the rest of `Scanner.cs` uses (you'll see the existing per-VM block — slot this in there). When KVP isn't available (VM off, no Integration Services, Linux without `hyperv-daemons`), return an empty string; **never throw** from this enrichment — if it fails for one VM, the rest of the scan must still succeed.

### Performance and failure modes

- KVP queries are local to the host and fast (~10-50 ms per VM in practice). Even a 30-VM host adds well under a second to the scan. Acceptable.
- Two known empty-string cases: (a) VM is `Off`, (b) Linux VM without `hyperv-daemons` / `linux-virtual` integration package. Both render the neutral fallback icon — no user-visible error.
- Do not block the scan on KVP failures. Wrap the per-VM KVP fetch in a `try { } catch { }` and continue.

## Frontend

### New `vmOsIcon(guestOs)` helper

Near the existing host OS icon code, add a small mapper. Match on case-insensitive substring; return the same SVG markup used for host icons today, sized to 14×14 (one tighter than the host's 16×16 since it sits in a denser table). Reuse the existing host icon SVGs — don't introduce new artwork.

| Substring match (case-insensitive) | Icon |
|---|---|
| `windows` | Windows 4-square (blue) |
| `ubuntu` | Ubuntu orange circle |
| `debian` | Debian spiral |
| `centos` | CentOS |
| `red hat` / `rhel` | Red Hat |
| `linux` (generic, no more specific match above) | generic Linux glyph (reuse a sensible existing one) |
| empty / no match | grey 14×14 rounded square with `?` (`var(--text3)`) |

### VM-table column

Add a new leftmost column to **both** VM-table render sites:

- Top-level VM Table (`vVms()` / equivalent)
- Per-host page's VM card (the filtered table)

Column spec:
- Header: blank (just the column space).
- Cell content: `vmOsIcon(vm.guestOs)` — nothing else.
- Width: ~28 px; cells centred.
- No sort on this column (the column is purely visual; if the dev later wants sort-by-OS, group the icon next to the existing Name column instead).

The existing `Name` column stays right of the icon — they shouldn't merge. The new column does not change responsive behavior.

## Out of scope

- Surfacing the OS string anywhere except via the icon (tooltip on hover is fine if cheap; skip otherwise).
- A separate "OS" sortable text column. The icon is enough for at-a-glance scanning; sorting can come later if asked.
- Refreshing guest OS independently of a full scan.
- Detecting more than the 6 OS families above. Add more if you spot common ones in the lab, but don't go hunting.

## Open notes for the codebase side

- The KVP query in PowerShell sometimes returns the OS as `OSName` and sometimes as the more verbose `OSFullName`. If `OSName` is empty, fall back to `OSFullName` before giving up.
- If you find that the icon column visually crowds the `Name` column in tight viewports, the responsive collapse should drop the icon column first (not Name).
