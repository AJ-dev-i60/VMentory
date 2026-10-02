#!/usr/bin/env bash
# VMentory fleet (ENG-0016) — provision Proxmox nodes for the fleet view. Idempotent; review before running.
#
# Per node:
#   1. read-only  vmentory@pve!monitor      -> PVEAuditor on /       (the poller; never writes)
#   2. write      vmentory-ops@pve!migrate  -> VMentoryMigrate on /  (move/drain verbs ONLY, never the poller)
#   3. probe key  root, `restrict` + forced command /usr/local/sbin/vmentory-probe — read-only; prints the
#                 ZFS ARC cap, DMI serial/product and each volume's allocated bytes, which the PVE REST API
#                 does not expose to an audit-only token.
#
# New token secrets land in ~/.vmentory-pve-<node>-token / ~/.vmentory-pve-<node>-migrate-token (0600)
# on the machine that runs this. Nothing secret is printed. Existing secret files are never overwritten.
# Re-running only refreshes the probe script and re-asserts ACLs.
#
# Usage: ops/provision-fleet-nodes.sh node:address [node:address ...]
#   e.g. ops/provision-fleet-nodes.sh vega14:172.0.0.14 sirius16:172.0.0.16
set -euo pipefail
ADMIN_KEY=${ADMIN_KEY:-$HOME/.ssh/edgestudios_deploy}
S=(ssh -o BatchMode=yes -i "$ADMIN_KEY")
KEY=${PROBE_KEY:-$HOME/.vmentory-pve-probe-key}
[ -f "$KEY" ] || ssh-keygen -q -t ed25519 -N '' -C 'vmentory-fleet-probe' -f "$KEY"
PUB=$(cat "$KEY.pub")
PRIVS="VM.Allocate VM.Migrate VM.Audit VM.Config.Disk VM.Config.CPU VM.Config.Memory VM.Config.Network VM.Config.Options VM.Config.HWType VM.Config.CDROM VM.Config.Cloudinit VM.PowerMgmt Datastore.AllocateSpace Datastore.Audit SDN.Use Sys.Incoming Sys.Audit"

read -r -d '' PROBE <<'P' || true
#!/bin/sh
# VMentory fleet probe — the only command the vmentory-fleet-probe key may run. Read-only.
# key=value facts, then one tab-separated line per guest volume with its ALLOCATED bytes (what a
# copy actually moves) — the PVE audit role cannot read per-volume usage without VM.Config.Disk.
echo "arc_c_max=$(awk '$1=="c_max"{print $3}' /proc/spl/kstat/zfs/arcstats 2>/dev/null)"
echo "serial=$(cat /sys/class/dmi/id/product_serial 2>/dev/null)"
echo "product=$(cat /sys/class/dmi/id/product_name 2>/dev/null)"
# storage config facts the audit role cannot read over REST (/storage/{id} needs Datastore.Allocate)
awk '/^[a-z]+:/{t=$1; sub(":","",t); id=$2; print "cfg\t" id "\ttype=" t; next} id!="" && ($1=="pool"||$1=="vgname"||$1=="path"||$1=="sparse"||$1=="thinpool"){print "cfg\t" id "\t" $1 "=" $2}' /etc/pve/storage.cfg 2>/dev/null
# ZFS: logical bytes referenced (uncompressed — what zfs send / drive-mirror transfers)
command -v zfs >/dev/null 2>&1 && zfs list -Hp -t volume,filesystem -o name,logicalreferenced 2>/dev/null | awk -F'\t' '{print "zfs\t"$1"\t"$2}'
# LVM: thin LVs = size x data_percent; thick LVs = their full size
command -v lvs >/dev/null 2>&1 && lvs --noheadings --nosuffix --units b --separator '|' -o vg_name,lv_name,lv_size,data_percent 2>/dev/null \
  | awk -F'|' '{gsub(/ /,"",$1); gsub(/ /,"",$2); gsub(/ /,"",$4); a=($4=="")?$3:$3*$4/100; printf "lvm\t%s/%s\t%.0f\n",$1,$2,a}'
# directory stores: real on-disk bytes of each image file
awk '/^[a-z]+:/{s=($1=="dir:");next} s&&$1=="path"{print $2;s=0}' /etc/pve/storage.cfg 2>/dev/null | while read -r p; do
  [ -d "$p/images" ] && find "$p/images" -type f \( -name '*.raw' -o -name '*.qcow2' -o -name '*.vmdk' \) -exec du -B1 {} + 2>/dev/null | awk -F'\t' '{print "file\t"$2"\t"$1}'
done
exit 0
P

[ $# -gt 0 ] || { echo "usage: $0 node:address ..." >&2; exit 2; }
for pair in "$@"; do
  n=${pair%%:*}; h=${pair#*:}
  echo "== $n ($h)"
  printf '%s\n' "$PROBE" | "${S[@]}" root@"$h" 'cat > /usr/local/sbin/vmentory-probe && chmod 755 /usr/local/sbin/vmentory-probe'
  out=$("${S[@]}" root@"$h" "PUB='$PUB' PRIVS='$PRIVS' bash -s" <<'EOF'
set -e
grep -q 'vmentory-fleet-probe' /root/.ssh/authorized_keys || echo "restrict,command=\"/usr/local/sbin/vmentory-probe\" $PUB" >> /root/.ssh/authorized_keys
pveum user list --output-format json | grep -q '"vmentory@pve"' || pveum user add vmentory@pve --comment "VMentory read-only poller"
pveum acl modify / --users vmentory@pve --roles PVEAuditor
pveum user token list vmentory@pve --output-format json | grep -q '"monitor"' || pveum user token add vmentory@pve monitor --privsep 0 --comment "VMentory estate dashboard" --output-format json
pveum role list --output-format json | grep -q '"VMentoryMigrate"' || pveum role add VMentoryMigrate --privs "$PRIVS"
pveum user list --output-format json | grep -q '"vmentory-ops@pve"' || pveum user add vmentory-ops@pve --comment "VMentory migrate/drain verbs only"
pveum acl modify / --users vmentory-ops@pve --roles VMentoryMigrate
pveum user token list vmentory-ops@pve --output-format json | grep -q '"migrate"' || pveum user token add vmentory-ops@pve migrate --privsep 0 --comment "VMentory move/drain" --output-format json
EOF
)
  printf '%s\n' "$out" | python3 -c '
import sys,json,os
n=sys.argv[1]
for line in sys.stdin:
    line=line.strip()
    if not line.startswith("{"): continue
    d=json.loads(line); tid=d["full-tokenid"]; v=d["value"]
    kind="migrate" if "vmentory-ops" in tid else "monitor"
    p=os.path.expanduser(f"~/.vmentory-pve-{n}-token" if kind=="monitor" else f"~/.vmentory-pve-{n}-migrate-token")
    if os.path.exists(p): print("   ", kind, "secret file already exists - NOT overwritten:", p); continue
    fd=os.open(p, os.O_WRONLY|os.O_CREAT|os.O_EXCL, 0o600); os.write(fd, f"{tid}={v}\n".encode()); os.close(fd)
    print("   saved", kind, "token ->", p)
' "$n"
  ssh -o BatchMode=yes -o IdentitiesOnly=yes -o IdentityAgent=none -o StrictHostKeyChecking=accept-new -i "$KEY" root@"$h" 2>&1 | awk -F'\t' 'NF<3{print "   probe: "$0; next} {c[$1]++} END{for (k in c) print "   probe: " c[k] " " k " volume lines"}'
done
