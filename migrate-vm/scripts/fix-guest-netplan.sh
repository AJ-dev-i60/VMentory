#!/usr/bin/env bash
# fix-guest-netplan.sh — Proxmox-side fix for the #1 Linux Hyper-V->Proxmox migration trap:
# the guest's netplan is pinned to interface name 'eth0' but the virtio NIC is 'ens18', so
# boot hangs on "Wait for Network to be Configured". This mounts the (stopped) VM's root disk
# from the host and rewrites netplan to MATCH BY MAC, then detaches cleanly.
#
# Handles both layouts: root on LVM-inside-a-partition (Ubuntu default) and a plain filesystem.
# Run on the Proxmox host as root, with the target VM STOPPED.
#
# Usage:
#   fix-guest-netplan.sh --vmid 171 --mac 00:15:5d:bf:46:0d --ip 172.0.0.71/24 \
#       --gw 172.0.0.13 --dns 8.8.8.8 [--disk /dev/vmdata-vg/vm-171-disk-1]
#
set -euo pipefail

VMID=""; MAC=""; IPCIDR=""; GW=""; DNS="8.8.8.8"; DISK=""
while [ $# -gt 0 ]; do case "$1" in
  --vmid) VMID="$2"; shift 2;;
  --mac)  MAC="$2"; shift 2;;
  --ip)   IPCIDR="$2"; shift 2;;
  --gw)   GW="$2"; shift 2;;
  --dns)  DNS="$2"; shift 2;;
  --disk) DISK="$2"; shift 2;;
  *) echo "unknown arg: $1" >&2; exit 2;;
esac; done
[ -n "$VMID$DISK" ] || { echo "need --vmid or --disk" >&2; exit 2; }
[ -n "$MAC" ] && [ -n "$IPCIDR" ] && [ -n "$GW" ] || { echo "need --mac, --ip, --gw" >&2; exit 2; }
MAC="$(echo "$MAC" | tr 'A-F' 'a-f')"

# Safety: the VM must be stopped before mounting its disk.
if [ -n "$VMID" ] && command -v qm >/dev/null; then
  [ "$(qm status "$VMID" 2>/dev/null)" = "status: stopped" ] || { echo "VM $VMID is not stopped — refusing to mount a live disk." >&2; exit 1; }
fi
# Resolve the scsi0 disk if --disk not given.
if [ -z "$DISK" ]; then
  vol="$(qm config "$VMID" | sed -n 's/^scsi0: *\([^,]*\).*/\1/p')"   # e.g. vmdata:vm-171-disk-1
  store="${vol%%:*}"; name="${vol##*:}"
  DISK="$(pvesm path "$store:$name" 2>/dev/null || echo "/dev/${store%-*}*/$name")"
fi
[ -b "$DISK" ] || { echo "disk not found: $DISK" >&2; exit 1; }
echo "Guest disk: $DISK"

# Find the largest partition (the root) and its start sector.
read -r START TYPE < <(fdisk -l "$DISK" 2>/dev/null | awk '/^\/dev|^'"${DISK//\//\\/}"'/{print $2, $0}' \
  | awk '{s=$1; sz=$5; line=$0; if (sz+0>max){max=sz+0; outs=s; outl=line}} END{print outs, "x"}')
# Fallback: ask sfdisk for the largest partition start.
if [ -z "${START:-}" ]; then
  START="$(sfdisk -d "$DISK" | awk -F'[ ,=]+' '/start=/{print $3, $0}' | sort -k1 -n | tail -1 | awk '{print $1}')"
fi
[ -n "$START" ] || { echo "could not determine root partition start sector; pass --disk and inspect with: fdisk -l $DISK" >&2; exit 1; }
echo "Root partition start sector: $START"

LOOP="$(losetup -f --show -o $((START*512)) "$DISK")"
echo "loop: $LOOP"
MNT=/mnt/guestroot; mkdir -p "$MNT"
VG=""
cleanup() { sync; umount "$MNT" 2>/dev/null || true; [ -n "$VG" ] && vgchange -an "$VG" >/dev/null 2>&1 || true; losetup -d "$LOOP" 2>/dev/null || true; }
trap cleanup EXIT

if blkid "$LOOP" 2>/dev/null | grep -q LVM2_member; then
  pvscan --cache "$LOOP" >/dev/null 2>&1 || true
  VG="$(pvs --noheadings -o vg_name "$LOOP" 2>/dev/null | tr -d ' ')"
  echo "guest VG: $VG"; vgchange -ay "$VG" >/dev/null
  mounted=""
  for lv in /dev/$VG/*; do
    if mount "$lv" "$MNT" 2>/dev/null; then
      if [ -d "$MNT/etc/netplan" ]; then echo "root LV: $lv"; mounted=1; break; else umount "$MNT"; fi
    fi
  done
  [ -n "$mounted" ] || { echo "could not find a root LV with /etc/netplan in $VG" >&2; exit 1; }
else
  mount "$LOOP" "$MNT"
fi

[ -d "$MNT/etc/netplan" ] || { echo "no /etc/netplan on the mounted root — is this Ubuntu/netplan?" >&2; exit 1; }
TARGET="$(ls "$MNT"/etc/netplan/*.yaml 2>/dev/null | head -1)"
[ -n "$TARGET" ] || TARGET="$MNT/etc/netplan/00-migrate.yaml"
echo "writing $TARGET"
[ -f "$TARGET" ] && cp -a "$TARGET" "$TARGET.premigrate.bak"
cat > "$TARGET" <<YAML
# Rewritten by migrate-vm: match NIC by MAC (interface name changes under KVM: eth0 -> ens18).
network:
  version: 2
  ethernets:
    primary:
      match:
        macaddress: $MAC
      addresses:
        - $IPCIDR
      gateway4: $GW
      nameservers:
        addresses: [$DNS]
YAML
chmod 600 "$TARGET"
echo "=== new netplan ==="; cat "$TARGET"

# Belt-and-suspenders: ensure cloud-init won't overwrite networking.
if [ -d "$MNT/etc/cloud/cloud.cfg.d" ] && ! grep -rsq "config: disabled" "$MNT/etc/cloud/cloud.cfg.d/"; then
  echo 'network: {config: disabled}' > "$MNT/etc/cloud/cloud.cfg.d/99-migrate-disable-net.cfg"
  echo "(disabled cloud-init network management)"
fi

echo "Done. Detaching and leaving VM stopped — start it with the NIC isolated first:"
echo "  qm set $VMID --net0 virtio=$MAC,bridge=vmbr0,link_down=1 && qm start $VMID  # verify on console"
echo "  qm set $VMID --net0 virtio=$MAC,bridge=vmbr0                                # link up AFTER source confirmed off"
