---
name: migrate-vm
description: >-
  Cold-migrate a virtual machine from Hyper-V to Proxmox VE, end to end, driving the whole
  process interactively over SSH and PowerShell. Use this whenever someone wants to move,
  migrate, import, convert, or "get a server off" Hyper-V onto Proxmox / KVM / QEMU — including
  phrasings like "migrate my Hyper-V VMs", "move this VM to Proxmox", "import a VHDX/VHD",
  "V2V from Hyper-V", "bring our Hyper-V guests over to the new Proxmox box", or "convert a
  Hyper-V VM". It handles shutting down and merging checkpoints on the Hyper-V side,
  transferring the disk, building a matching Proxmox VM (UEFI/OVMF or legacy BIOS), importing
  the disk, fixing the Linux NIC-name/netplan trap that hangs boot, and verifying the result
  WITHOUT causing a MAC/IP conflict with the still-running original. Reach for this skill even
  if the user only says "I want to migrate a VM" and mentions Hyper-V and Proxmox in context.
---

# Migrate a VM from Hyper-V to Proxmox VE

This skill performs a **cold migration** (the guest is shut down for the cutover) of a Hyper-V
VM onto a Proxmox VE host. Proxmox/QEMU reads Hyper-V's `VHDX`/`VHD` disks natively, so the
core is: shut down → merge checkpoints → copy the disk → build a matching VM → import the disk
→ fix the guest's network → verify safely.

You are driving this interactively. Gather the inputs, run each step, and **verify before
moving on**. Never barrel through — a migration touches production, and most failures are
silent (a VM that "boots" but is really the original answering, or a guest that boots with no
network). The two rules below exist because skipping them has burned this exact workflow.

## ⚠️ Two rules that override convenience

**1. The verification rule — prove identity, don't assume it.**
The migrated copy is built with the *same MAC and same IP* as the source (on purpose — it keeps
the guest's networking working). That means **ping and ARP cannot tell the copy apart from the
original.** A "successful" ping may be the still-running source. So:
- Before trusting *any* runtime check, confirm the **source is powered off** — prove it by
  showing the IP goes **dark** when the copy is stopped/isolated.
- To test the copy's boot in isolation, start it with the NIC link down (`link_down=1`) and
  watch the **console**, not the network.

**2. Never run both at once.** Identical MAC + IP on the same L2 segment = an address conflict
that disrupts the live original. Bring the copy onto the network only after the source is
confirmed off. Keep the Hyper-V original intact (and disable its auto-start) as your rollback
until the copy is fully verified.

## Inputs to gather first

Ask for these up front (offer sensible defaults, confirm the risky ones):

| Input | Notes |
|---|---|
| Source Hyper-V host | IP/hostname; how you'll reach it (WinRM/PowerShell remoting or RDP). Needs admin creds — see `references/runbook.md` for the DPAPI method that keeps the password out of chat. |
| VM name | Exact name. **Names often contain brackets** (`AJ Linux Box [71]`) — brackets are PowerShell wildcards, so always select with `Get-VM \| Where-Object Name -eq '...'`, never `-Name '...[..]'`. |
| Proxmox target | SSH alias or `root@host` for the Proxmox node. Confirm you have key access. |
| Target storage | Proxmox storage for the disk (LVM-thin or dir). Confirm free space ≥ the disk's virtual size. |
| Target VMID | **Must be ≥ 100** (Proxmox reserves < 100). |
| Guest OS | Linux vs Windows changes the disk-controller / driver handling (see below). |
| Firmware | **Gen2 = UEFI → OVMF**; **Gen1 = BIOS → SeaBIOS**. Get it from `Get-VM \| Select Generation`. |
| NIC MAC | Preserve it on the Proxmox side so the guest keeps its IP. `Get-VMNetworkAdapter`. |
| Guest IP | Used only to verify reachability after cutover. |
| SMB read account | An account that can read the Hyper-V host's disk share (e.g. `G$`). |

## Running mode — manual hand-off vs. centralized

There are two ways to drive the Hyper-V side:

- **Manual hand-off (default, zero setup):** the driving machine usually can't authenticate to
  the domain Hyper-V hosts, so you ask the operator to run the inspect/shutdown/merge PowerShell
  (the snippets in `references/runbook.md`) and paste results. Works anywhere, but not hands-off.
- **Centralized on the Proxmox host (recommended once set up):** the Proxmox host runs the
  Hyper-V PowerShell itself over WinRM (`scripts/winrun.py`) and reads the disk over SMB, using
  one least-privilege service account that's a local admin on the Hyper-V hosts. No hand-offs.
  **Setup, the fleet-wide GPO, and the access-chain gotchas are in
  `references/centralized-access.md` — read it before relying on this mode.** If a `winrun.py`
  call returns 401, that file's gotcha list is your checklist (it's almost always
  "authenticated but not a local admin," or the NetBIOS-vs-FQDN domain form).

Check for `/root/.winrmcreds` on the Proxmox host: if present, prefer centralized mode; if a
WinRM call fails, fall back to asking the operator rather than stalling.

## Workflow

Work through these in order. Full commands, the DPAPI credential method, and the guest-disk
netplan-edit procedure live in `references/runbook.md` — read it when you reach those steps.
Helper scripts are in `scripts/`.

### 1. Hyper-V side: shut down cleanly and merge checkpoints
Run on the Hyper-V host (PowerShell). `scripts/hyperv-prep.ps1` does this; or do it inline.
- Select the VM with `Where-Object Name -eq`.
- **Shut it down cleanly.** For Linux guests, prefer an *in-guest* `shutdown -h now` — Hyper-V's
  graceful "Stop-VM" relies on the LIS integration daemon and often silently does nothing on
  Linux, leaving the VM running (which later masquerades as a "successful" migration).
- **Merge checkpoints:** `Get-VMSnapshot -VM $vm | Remove-VMSnapshot`. A running VM whose active
  disk is a `.avhdx` is sitting on a differencing/checkpoint disk; copying that alone gives a
  broken image. Merging collapses it into a flat `.vhdx`.
- Report the resulting flat disk path and run `qemu-img info` on it later to confirm
  `virtual size`, and **no backing file**.

### 2. Transfer the disk to the Proxmox host
- On the Proxmox host, mount the Hyper-V host's disk share over **CIFS read-only**, using a
  **root-only credentials file the user writes themselves** (never paste a password into chat).
  See the runbook for the exact `read -rsp` one-liner and the mount command. **`shred` the creds
  file afterward** (or keep it deliberately if migrating a batch — ask).
- No staging copy is needed: `qm importdisk` reads the VHDX straight off the mount.

### 3. Build the matching VM shell
```
qm create <vmid> --name <name> --cores <n> --memory <MB> --cpu host \
  --machine q35 --bios <ovmf|seabios> --scsihw virtio-scsi-single \
  --net0 virtio=<ORIGINAL_MAC>,bridge=<bridge> --ostype <l26|win...> --agent 1
# UEFI/Gen2 only — EFI disk WITHOUT pre-enrolled keys (Secure Boot off; avoids bootloader rejection):
qm set <vmid> --efidisk0 <storage>:1,efitype=4m,pre-enrolled-keys=0
```

### 4. Import and attach the disk
```
qm importdisk <vmid> "<path-to-vhdx>" <storage>      # converts VHDX → raw on the target
qm set <vmid> --scsi0 <storage>:vm-<vmid>-disk-1,iothread=1,discard=on --boot order=scsi0
```
(The imported disk lands as `unused0`; `vm-<vmid>-disk-1` assumes `disk-0` is the EFI disk.
Check `qm config <vmid>` and attach the right volume.)

### 5. Fix the guest network (the Linux trap that wastes hours)
A Linux guest's network config is usually **pinned to the interface name** (`eth0` on Hyper-V's
netvsc). Under KVM the virtio NIC comes up as `ens18`, so the config never matches, the interface
stays down, and boot **hangs forever on "Wait for Network to be Configured."** The disk boots
fine — it's purely a naming mismatch.

Fix it deterministically from the Proxmox host (VM stopped): mount the guest root and change the
network config to **match by MAC address** instead of by name (the MAC is preserved, so this is
stable). Most Ubuntu roots are on **LVM inside the partition** — activate the guest VG first.
Full procedure (including the LVM handling and clean detach) is in the runbook;
`scripts/fix-guest-netplan.sh` automates the netplan case.

For **Windows guests** the analogue is the VirtIO storage driver: attach the boot disk on SATA
first (or pre-install virtio-win in the guest before shutdown), then switch to VirtIO SCSI —
otherwise `INACCESSIBLE_BOOT_DEVICE`. See the runbook's Windows section.

### 6. Verify — safely (apply Rule 1)
- Confirm the source is **off**: with the copy stopped/isolated, the guest IP must be **dark**.
- Boot the copy **isolated** (`link_down=1`) and confirm via the **console** it reaches a login
  prompt. If it hangs on network-wait, step 5 isn't done.
- Only then bring the link up (`qm set <vmid> --net0 virtio=<MAC>,bridge=<bridge>`), and verify
  the guest answers on its IP and its services/ports are up. Because the source is off, the
  responder is provably the copy.
- **App layer:** containerized apps usually return on their own via their restart policy. If one
  doesn't, check whether it's a *pre-existing* issue (e.g. a stale cache from an overnight
  auto-update) rather than something the migration caused — diagnose before assuming.

### 7. Safety guards (apply throughout)
- Before any `mkfs`/`pvcreate` on a target device, **verify it's blank** (no partitions, no
  signature, expected size).
- Keep the Hyper-V original intact as rollback; **disable its auto-start** so a host reboot can't
  silently bring it back and collide with the copy.
- Match by **UUID**, not `/dev/sdX` — device letters reorder across reboots.

## When done
Summarize what was migrated, confirm source-off, and note the rollback (the dormant Hyper-V VM).
Only after the user confirms the copy is good in production should the original be decommissioned.

## Reference material
- `references/runbook.md` — full copy-pasteable command reference, the DPAPI credential method,
  the guest-disk netplan-edit (with LVM) procedure, and the Windows-guest variant. Read it when
  you reach steps 1, 2, and 5, or hand it to a human who wants to do this manually.
- `references/centralized-access.md` — how to run the whole migration from the Proxmox host
  (service account, WinRM/SMB, the fleet-wide GPO) and the access-chain gotchas. Read before
  using centralized mode or when a `winrun.py` call returns 401.
- `scripts/winrun.py` — run PowerShell on a Hyper-V host over WinRM from the Proxmox host
  (centralized mode engine).
- `scripts/hyperv-prep.ps1` — Hyper-V-side shutdown + checkpoint merge + flat-disk report.
- `scripts/fix-guest-netplan.sh` — Proxmox-side guest-root mount + netplan match-by-MAC fix.
