#!/opt/migrate-venv/bin/python
"""Run a PowerShell script on a remote Windows (Hyper-V) host via WinRM/NTLM,
from the Proxmox host. This is the engine of the "centralized on the Proxmox
host" mode: it lets the migration driver inspect/shutdown/merge on Hyper-V
without any manual PowerShell on the source side.

Usage:   winrun.py <host> [credfile]      # PowerShell script is read from stdin
Example: echo 'Get-VM | Select Name,State' | winrun.py 172.0.0.21

Creds file (default /root/.winrmcreds), three lines:
    user=svc-migrate
    domain=i60.local        # FQDN is fine here; NTLM uses the NetBIOS label
    pass=<password>

Prereqs on the Proxmox host (once):
    apt-get install -y python3-venv
    python3 -m venv /opt/migrate-venv
    /opt/migrate-venv/bin/pip install pywinrm requests-ntlm

The remote account must be a LOCAL ADMINISTRATOR on the Hyper-V host — WinRM only
admits Administrators / Remote Management Users, and the VHDX copy needs C$/D$.
A 401 here almost always means "authenticated but not a local admin," not a bad
password (test the password separately with: smbclient -L //<host> -A <smbcreds>).
"""
import sys
import winrm

credfile = sys.argv[2] if len(sys.argv) > 2 else "/root/.winrmcreds"
creds = {}
with open(credfile) as f:
    for line in f:
        line = line.rstrip("\n")
        if "=" in line:
            k, v = line.split("=", 1)
            creds[k] = v

host = sys.argv[1]
ps = sys.stdin.read()
# NTLM wants the NetBIOS domain (first label), not the DNS/FQDN form.
netbios = creds["domain"].split(".")[0]
user = "{}\\{}".format(netbios, creds["user"])

session = winrm.Session(
    "http://{}:5985/wsman".format(host),
    auth=(user, creds["pass"]),
    transport="ntlm",   # NTLM message-encrypts the payload, so HTTP/5985 is fine
)
r = session.run_ps(ps)
sys.stdout.write(r.std_out.decode("utf-8", "replace"))
err = r.std_err.decode("utf-8", "replace")
if err.strip():
    sys.stderr.write(err)
sys.exit(r.status_code)
