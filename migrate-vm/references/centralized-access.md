# Centralized mode: drive the whole migration from the Proxmox host

By default this skill hands the Hyper-V-side steps (inspect, shutdown, merge) back to a human
running PowerShell, because the driving machine usually can't authenticate to the domain
Hyper-V hosts. **Centralized mode removes those hand-offs**: the Proxmox host itself runs the
Hyper-V PowerShell over WinRM and reads the disk over SMB, using one service account. Set it up
once and every future migration is hands-off.

This file documents the setup and — importantly — the **access-chain gotchas**, each of which
silently blocks WinRM with a misleading error.

## The pieces
1. **A WinRM client on the Proxmox host** (`scripts/winrun.py` + a venv):
   ```bash
   apt-get install -y python3-venv smbclient
   python3 -m venv /opt/migrate-venv
   /opt/migrate-venv/bin/pip install pywinrm requests-ntlm
   ```
2. **A least-privilege service account** (e.g. `svc-migrate`) that is a **local Administrator on
   the Hyper-V hosts** (covers all three needs at once: WinRM access, Hyper-V cmdlets, and
   `C$`/`D$` disk read).
3. **Credentials stored on the Proxmox host**, root-only, written by a human (never pasted into
   a chat). One password feeds both WinRM and SMB:
   ```bash
   read -rp 'user: ' U; read -rp 'domain (e.g. i60): ' D; read -rsp 'pw: ' P
   printf 'user=%s\ndomain=%s\npass=%s\n'         "$U" "$D" "$P" > /root/.winrmcreds
   printf 'username=%s\ndomain=%s\npassword=%s\n' "$U" "$D" "$P" > /root/.smbcreds
   chmod 600 /root/.winrmcreds /root/.smbcreds; unset P
   ```
Then everything runs from the Proxmox host, e.g.:
```bash
echo "Get-VM | Where-Object Name -eq 'theo_linux [73]' | Select Name,State,Generation" \
  | /opt/migrate-venv/bin/python /root/winrun.py 172.0.0.21
```

## Granting the service account local admin — at fleet scale
Adding the account to local Administrators **per host by hand does not scale**. Use a domain
group + Group Policy:
1. `New-ADGroup -Name Proxmox-Migrators -GroupScope Global` and add the service account to it.
2. Put the Hyper-V hosts in a dedicated **OU**, and create+link a GPO to that OU that adds the
   group to local Administrators via **Computer Configuration → Preferences → Control Panel
   Settings → Local Users and Groups → Administrators (built-in) → Add `DOMAIN\Proxmox-Migrators`**.
3. Elevated `gpupdate /force` on each host (or wait for refresh).

For a handful of hosts, a one-time direct add is simpler than GPO and still "fleet" in one loop
(run with a domain-admin cred, then revert to the service account):
```bash
for h in 172.0.0.21 172.0.0.103; do
  echo "Add-LocalGroupMember -Group Administrators -Member 'i60\Proxmox-Migrators'" \
    | /opt/migrate-venv/bin/python /root/winrun.py $h /root/.adminwinrm
done
```

## The access-chain gotchas (each one cost real time)
Work through these in order when WinRM returns **401 / "credentials were rejected"**:

1. **It's usually authorization, not the password.** pywinrm reports an authenticated-but-not-
   -allowed user as "credentials rejected." Prove the password is fine independently:
   `smbclient -L //<host> -A /root/.smbcreds` — if it lists shares, the password is correct and
   the 401 is a *rights* problem. Confirm the rights gap with `smbclient //<host>/C$ -A
   /root/.smbcreds -c ls` → `NT_STATUS_ACCESS_DENIED` means the account is **not a local admin**.
2. **NTLM needs the NetBIOS domain, not the FQDN.** `i60\user` works; `i60.local\user` 401s.
   (`winrun.py` already strips to the first label. SMB tolerates the FQDN, which is why SMB can
   succeed while WinRM fails with the same creds file.)
3. **The account must be a local admin on the host** — being a valid domain user is not enough.
   WinRM only admits `Administrators` / `Remote Management Users`.
4. **A computer in the default `CN=Computers` container gets no OU-linked GPOs.** You cannot link
   a GPO to that container. Move the host into a real **OU** (`Move-ADObject`) that the GPO is
   linked to. Verify with `gpresult /r /scope:computer` → the "CN=…,OU=…" line.
5. **`gpupdate /target:computer` must be run elevated** or it silently skips computer policy —
   so the local-admin grant never processes. Look for "Computer Policy update has completed
   successfully."
6. **"Created a GPO" ≠ "linked a GPO."** Editing a GPO in the *Group Policy Objects* folder does
   nothing until it's linked to the OU. Use the OU's right-click **"Create a GPO … and Link it
   here."** Confirm from the Linux side via LDAP: the OU's `gPLink` attribute should name it, and
   `gpresult` should list it under "Applied Group Policy Objects."
7. **The GPP member must resolve** — enter it as `DOMAIN\Proxmox-Migrators`, and the preference
   must live under **Computer** Configuration (not User).

Quick fleet-side LDAP checks (run from the Proxmox host with any read account, e.g. the AD-realm
bind account over LDAPS) — handy because they work even while WinRM is still locked out:
```bash
# where does a host's computer object live? (find its name with: nmblookup -A <ip>)
ldapsearch -H ldaps://dc1.<dom>:636 -x -D <bind> -w "$PW" -b "DC=...,DC=..." "(sAMAccountName=NAME$)" distinguishedName
# what GPOs are linked to the OU?
ldapsearch ... -b "OU=Hyper-V Hosts,DC=...,DC=..." -s base gPLink
# list all GPOs (GUID -> displayName)
ldapsearch ... -b "CN=Policies,CN=System,DC=...,DC=..." "(objectClass=groupPolicyContainer)" cn displayName
```

## Verify the chain is healthy
A successful `Get-VM` over `winrun.py` means all of the above is in place. From then on the
driver inspects, shuts down (prefer in-guest `shutdown -h now` for Linux), merges checkpoints,
mounts the disk share, imports, builds, fixes networking, and verifies — with no hand-offs.
