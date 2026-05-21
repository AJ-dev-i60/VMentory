# Hyper-V Inventory Dashboard

A portable, single-exe Windows tool that inventories Hyper-V hosts over WinRM and displays results in a modern local web dashboard. No installer, no persistent storage — all data lives in RAM and is wiped on exit.

## Building

### Prerequisites
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)

### Build (development run)
```powershell
cd hyper-inventory
dotnet run
```

### Build single-file exe (release)
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./dist
```
Output: `dist\hyper-inventory.exe` (~80 MB, no dependencies)

## Running

```
hyper-inventory.exe [--mock]
```

- Binds to `127.0.0.1` on a random free port
- Opens the system default browser automatically
- Shows the URL and session token in the console

### Mock mode
```
hyper-inventory.exe --mock
```
Loads 5 simulated hosts (varied states, some unreachable, one with auth failure). No real WinRM calls.

### Console keys
| Key | Action |
|-----|--------|
| `Q` | Purge all data and quit |
| `R` | Re-open browser to current session |

## Target host requirements (Hyper-V servers)

### Enable WinRM on each target host
Run as Administrator on each Hyper-V host:
```powershell
Enable-PSRemoting -Force
```

### For non-domain-joined client machines
The client running `hyper-inventory.exe` must trust the target host. Run once per target host (or use `*` for all):
```powershell
# On the CLIENT machine running hyper-inventory.exe
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "hv-prod-01,hv-prod-02,192.168.1.10" -Force

# Or to trust all hosts (less secure):
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "*" -Force
```

Check current TrustedHosts:
```powershell
Get-Item WSMan:\localhost\Client\TrustedHosts
```

### WinRM authentication
Uses **Negotiate** (Kerberos on domain-joined machines, NTLM fallback for workgroup/non-domain). No Basic auth or CredSSP required.

### Firewall
WinRM HTTP uses **TCP 5985** (default). Ensure this port is open between the client machine and all target Hyper-V hosts.

## Troubleshooting

| Error | Cause | Fix |
|-------|-------|-----|
| ICMP ✗, WinRM ✗ | Host unreachable | Check firewall, IP address |
| ICMP ✓, WinRM ✗ | WinRM not enabled / wrong port | Run `Enable-PSRemoting -Force` on target |
| WinRM ✓, Auth ✗ | Wrong credentials or not in TrustedHosts | Verify creds; add to TrustedHosts if non-domain |
| Auth ✓, Scan error | Hyper-V module not found | Target host is not a Hyper-V server |
| "Access denied" | Credentials not in local admins on target | Add account to Hyper-V Administrators group |

### Test WinRM manually
```powershell
Test-WSMan -ComputerName hv-prod-01 -Authentication Negotiate -Credential (Get-Credential)
```

## Security notes
- Binds to `127.0.0.1` only — never exposed to the network
- All API requests require a per-session token (regenerated each launch)
- Credentials held as byte arrays, zeroed on dispose / session end
- No scan data ever written to disk; only `errors.log` (no PII/host data)
- All HTTP responses set `Cache-Control: no-store, no-cache`
