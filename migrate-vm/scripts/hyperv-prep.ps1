<#
.SYNOPSIS
  Hyper-V side prep for a Hyper-V -> Proxmox migration: inspect, shut down cleanly,
  merge any checkpoint (.avhdx) into a flat .vhdx, and report the resulting disk.

.DESCRIPTION
  Run from a workstation that can reach the Hyper-V host (uses PowerShell remoting),
  or directly on the Hyper-V host (omit -HyperVHost). VM names containing brackets are
  handled correctly (selected with Where-Object -eq, not -Name wildcards).

  This does NOT delete the source VM — it leaves it shut down and intact as a rollback.

.EXAMPLE
  .\hyperv-prep.ps1 -VMName 'AJ Linux Box [71]' -HyperVHost 172.0.0.103
  .\hyperv-prep.ps1 -VMName 'web01' -Merge:$false       # inspect only, don't touch
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $VMName,
  [string] $HyperVHost,                 # omit to run locally on the Hyper-V host
  [switch] $Merge = $true,              # merge checkpoints after shutdown
  [switch] $ForceStop = $false          # hard Stop-VM -Force if graceful stalls
)

$work = {
  param($VMName, $Merge, $ForceStop)
  $vm = Get-VM | Where-Object Name -eq $VMName
  if (-not $vm) { throw "VM '$VMName' not found on $(hostname)" }

  "=== VM ==="
  $vm | Select-Object Name,State,Generation,ProcessorCount,
        @{n='MemGB';e={[math]::Round($_.MemoryStartup/1GB,1)}},DynamicMemoryEnabled | Format-List
  if ($vm.Generation -eq 2) { "=== Firmware ==="; Get-VMFirmware $vm | Select-Object SecureBoot,SecureBootTemplate | Format-List }
  "=== Network (preserve this MAC on Proxmox) ==="
  Get-VMNetworkAdapter $vm | Select-Object Name,MacAddress,SwitchName | Format-List

  if ($vm.State -ne 'Off') {
    "Shutting down '$VMName' (graceful)..."
    Stop-VM -VM $vm -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(120)
    do { Start-Sleep 3; $vm = Get-VM -Id $vm.Id } while ($vm.State -ne 'Off' -and (Get-Date) -lt $deadline)
    if ($vm.State -ne 'Off') {
      if ($ForceStop) { "Graceful stop stalled; forcing power off."; Stop-VM -VM $vm -TurnOff -Force }
      else { throw "VM did not shut down within 120s. For Linux guests, run 'sudo shutdown -h now' inside the guest (Hyper-V graceful needs the LIS daemon), or re-run with -ForceStop." }
    }
  }
  "VM is Off."

  if ($Merge) {
    $snaps = Get-VMSnapshot -VM $vm
    if ($snaps) { "Merging $($snaps.Count) checkpoint(s) into the base disk..."; $snaps | Remove-VMSnapshot; Start-Sleep 5 }
    else { "No checkpoints to merge." }
  }

  "=== Resulting disk(s) — copy the .vhdx path(s) to the Proxmox side ==="
  Get-VMHardDiskDrive -VM $vm | ForEach-Object {
    $v = Get-VHD $_.Path
    [pscustomobject]@{ Path=$_.Path; Format=$v.VhdFormat; Type=$v.VhdType
      VirtualGB=[math]::Round($v.Size/1GB,1); ActualGB=[math]::Round($v.FileSize/1GB,1) }
  } | Format-List
  "NOTE: paths must end in .vhdx (not .avhdx). If still .avhdx, the merge did not complete."
}

if ($HyperVHost) {
  $s = New-PSSession -ComputerName $HyperVHost -Credential (Get-Credential)
  try { Invoke-Command -Session $s -ScriptBlock $work -ArgumentList $VMName,$Merge,$ForceStop }
  finally { Remove-PSSession $s }
} else {
  & $work $VMName $Merge $ForceStop
}
