# Hyper-V → Proxmox migration runbook

Full command reference for the workflow in `SKILL.md`. Usable by Claude when it reaches the
detailed steps, or by a human doing this by hand. Replace `<...>` placeholders. Validated
2026-06-12 migrating an Ubuntu 22.04 Gen2 guest from a Hyper-V host to a Proxmox node.

## Table of contents
1. Inspect the source VM (Hyper-V)
2. Credentials without pasting passwords (DPAPI / CIFS creds file)
3. Shut down + merge checkpoints (Hyper-V)
4. Transfer the disk (CIFS mount on Proxmox)
5. Build the Proxmox VM + import the disk
6. Fix the guest network — the LVM + netplan procedure (Linux)
7. Windows-guest variant (VirtIO drivers)
8. Verify safely
9. Cleanup & rollback

---

## 1. Inspect the source VM (Hyper-V)

Run in a PowerShell session that can reach the Hyper-V host. Note: VM names with brackets are
PowerShell wildcards — always use `Where-Object Name -eq`, never `-Name 'x[1]'`.

```powershell
$s = New-PSSession -ComputerName <hyperv-ip> -Credential (Get-Credential)
Invoke-Command -Session $s {
  $vm = Get-VM | Where-Object Name -eq '<VM NAME>'
  $vm | Select Name,State,Generation,ProcessorCount,
        @{n='MemGB';e={[math]::Round($_.MemoryStartup/1GB,1)}},DynamicMemoryEnabled | Format-List
  Get-VMFirmware $vm | Select SecureBoot,SecureBootTemplate | Format-List      # Gen2 only
  Get-VMHardDiskDrive $vm | ForEach-Object { $v=Get-VHD $_.Path;
    [pscustomobject]@{Path=$_.Path;Ctrl="$($_.ControllerType)$($_.ControllerNumber):$($_.ControllerLocation)";
      VirtGB=[math]::Round($v.Size/1GB,1);ActualGB=[math]::Round($v.FileSize/1GB,1);Type=$v.VhdType} } | Format-List
  Get-VMNetworkAdapter $vm | Select Name,MacAddress,SwitchName | Format-List
}
```
Capture: Generation (→ OVMF/SeaBIOS), CPU/RAM, **disk path** (note `.avhdx` = checkpoint to
merge), **MAC** (to preserve), and disk virtual size (→ target storage free space).

## 2. Credentials without pasting passwords

**WinRM to the Hyper-V host (from a non-domain workstation):** add it to TrustedHosts once, and
hand the credential to Claude via a DPAPI-encrypted file that only the same user on the same
machine can read:
```powershell
Get-Credential <domain>\<admin> | Export-CliXml $env:USERPROFILE\hv.cred.xml   # you run this (prompts hidden)
# Claude/automation then:  $cred = Import-CliXml $env:USERPROFILE\hv.cred.xml
```

**CIFS read account on the Proxmox host** — the user writes the creds file directly on the
Proxmox host so the password never enters chat or shell history:
```bash
# on the Proxmox host:
read -rsp 'SMB password: ' PW; printf 'username=<user>\ndomain=<dom>\npassword=%s\n' "$PW" \
  > /root/.smbcreds; chmod 600 /root/.smbcreds; unset PW; echo; echo STORED
```
`shred -u /root/.smbcreds` when finished (or keep it for a batch — decide explicitly).

## 3. Shut down + merge checkpoints (Hyper-V)

```powershell
Invoke-Command -Session $s {
  $vm = Get-VM | Where-Object Name -eq '<VM NAME>'
  # Linux: prefer in-guest `sudo shutdown -h now`. Hyper-V graceful Stop-VM needs the LIS daemon
  # and often no-ops on Linux. Fall back to: Stop-VM -VM $vm  (add -Force only if it hangs).
  do { Start-Sleep 3; $vm = Get-VM -Id $vm.Id } until ($vm.State -eq 'Off')
  Get-VMSnapshot -VM $vm | Remove-VMSnapshot          # merges .avhdx → flat .vhdx (no output = none)
  Start-Sleep 5
  Get-VMHardDiskDrive -VM $vm | ForEach-Object { Get-VHD $_.Path } |
    Select Path,VhdType,@{n='ActualGB';e={[math]::Round($_.FileSize/1GB,1)}}
}
```
The reported `Path` must now end in `.vhdx` (not `.avhdx`).

## 4. Transfer the disk (CIFS mount on Proxmox)

```bash
mkdir -p /mnt/import
mount -t cifs "//<hyperv-ip>/<share e.g. G$>" /mnt/import \
  -o credentials=/root/.smbcreds,ro,vers=3.0,iocharset=utf8
ls -lh "/mnt/import/<path>/"                                  # find the flat .vhdx
qemu-img info "/mnt/import/<path>/<disk>.vhdx"                # confirm virtual size + NO backing file
```

## 5. Build the Proxmox VM + import the disk

```bash
qm create <vmid> --name <name> --cores <n> --memory <MB> --sockets 1 --cpu host \
  --machine q35 --bios <ovmf|seabios> --scsihw virtio-scsi-single \
  --net0 virtio=<MAC>,bridge=<bridge> --ostype <l26|win10|win11|...> --agent 1
# UEFI/Gen2 only:
qm set <vmid> --efidisk0 <storage>:1,efitype=4m,pre-enrolled-keys=0

qm importdisk <vmid> "/mnt/import/<path>/<disk>.vhdx" <storage>
qm config <vmid> | grep -E 'unused|efidisk'                  # find the imported volume name
qm set <vmid> --scsi0 <storage>:vm-<vmid>-disk-1,iothread=1,discard=on --boot order=scsi0
```
Guards: VMID ≥ 100; before `mkfs`/`pvcreate` on any *new storage* device, confirm it's blank
(`lsblk` shows no children, `blkid` empty, size matches).

## 6. Fix the guest network — LVM + netplan (Linux)

Symptom: boot hangs on **"Wait for Network to be Configured (no limit)"**, or reaches login but
has no network. Cause: the netplan config names `eth0`; the virtio NIC is `ens18`. Fix: edit the
guest's netplan to **match by MAC**. Do it from the Proxmox host with the VM **stopped**.

```bash
qm stop <vmid>
DISK=/dev/<vg>/vm-<vmid>-disk-1                  # the LV backing scsi0
fdisk -l "$DISK"                                 # find the root partition's start sector

# Map the root partition via a loop device at (start_sector * 512):
LOOP=$(losetup -f --show -o $((<ROOT_START_SECTOR>*512)) "$DISK")

# Ubuntu root is usually LVM. If the partition is type LVM2_member:
pvscan --cache "$LOOP" >/dev/null 2>&1
VG=$(pvs --noheadings -o vg_name "$LOOP" | tr -d ' ')        # e.g. ubuntu-vg (won't collide with host VGs)
vgchange -ay "$VG"
mkdir -p /mnt/guestroot
for lv in /dev/$VG/*; do mount "$lv" /mnt/guestroot 2>/dev/null && \
  [ -d /mnt/guestroot/etc/netplan ] && break || umount /mnt/guestroot 2>/dev/null; done
# (If the partition is a plain filesystem, just: mount "$LOOP" /mnt/guestroot)

# Inspect, back up, and rewrite the netplan to match by MAC (keep the existing IP/gw/DNS):
cat /mnt/guestroot/etc/netplan/*.yaml
cp -a /mnt/guestroot/etc/netplan/<file>.yaml /mnt/guestroot/etc/netplan/<file>.yaml.bak
cat > /mnt/guestroot/etc/netplan/<file>.yaml <<'YAML'
network:
  version: 2
  ethernets:
    primary:
      match:
        macaddress: <lower:00:15:5d:..:..:..>
      addresses:
        - <ip>/<cidr>
      gateway4: <gw>          # 22.04 still accepts this; newer prefer routes: - to: default / via: <gw>
      nameservers:
        addresses: [<dns>]
YAML
chmod 600 /mnt/guestroot/etc/netplan/<file>.yaml

# Clean detach BEFORE starting the VM (or the host holds the disk):
sync; umount /mnt/guestroot; vgchange -an "$VG"; losetup -d "$LOOP"
```
Notes:
- Matching by MAC (not `set-name`) means the interface keeps its `ens18` name but gets the
  config — robust and avoids rename-timing issues.
- If cloud-init manages networking, it can clobber this. Check
  `/etc/cloud/cloud.cfg.d/` — Ubuntu Server installs often ship
  `subiquity-disable-cloudinit-networking.cfg` (`network: {config: disabled}`), in which case
  you're safe. Otherwise add that file too.
- Alternative one-liner fix if you prefer not to touch netplan: append `net.ifnames=0
  biosdevname=0` to the kernel cmdline so the NIC becomes `eth0` again (edit
  `/etc/default/grub` + `update-grub` in a chroot, or at the GRUB menu for one boot).

## 7. Windows-guest variant

Windows BSODs `INACCESSIBLE_BOOT_DEVICE` if the boot disk is VirtIO SCSI with no driver. Either:
- **Before shutdown on Hyper-V:** install the virtio-win guest tools, then it just works; or
- **On Proxmox:** attach the boot disk as **SATA** first (`--sata0` instead of `--scsi0`), boot,
  install virtio-win from the ISO inside Windows, shut down, switch the disk to `--scsi0`
  (virtio-scsi) for performance.
Also set `--ostype` to the matching Windows version, and for Gen2 keep `--bios ovmf` + efidisk.

## 8. Verify safely (the rule that matters most)

The copy has the **same MAC + IP** as the source — ping/ARP cannot distinguish them.
```bash
# Prove the source is OFF: with the copy stopped (or link_down=1), the IP must be dark:
qm set <vmid> --net0 virtio=<MAC>,bridge=<bridge>,link_down=1   # isolate
qm start <vmid>
ping -c4 -W1 <guest-ip>            # MUST be 100% loss → source confirmed off; no conflict
# Watch the *console* (web UI → VM → Console) to confirm it boots to a login prompt.
# Only now bring it onto the network:
qm set <vmid> --net0 virtio=<MAC>,bridge=<bridge>               # link up (live)
ping -c2 <guest-ip>; for p in 22 80 443; do (echo >/dev/tcp/<guest-ip>/$p) 2>/dev/null \
  && echo "$p open"; done          # now provably the copy, since the source is off
```

## 9. Cleanup & rollback
- `shred -u /root/.smbcreds`; `umount /mnt/import`.
- Leave the Hyper-V original **shut down and intact** as rollback; **disable its auto-start**
  (Hyper-V → VM settings → Automatic Start Action → Nothing) so a host reboot can't revive it
  into a MAC/IP conflict.
- Decommission the original only after the copy is confirmed good in production.
