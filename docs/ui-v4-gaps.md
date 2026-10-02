# UI v4 (Guest Browser) — what the design asks for that VMentory cannot provide yet

**Design:** claude.ai/design project `81945b7d…`, file *Proxmox Inventory - Final Wireframes.dc.html*
(wireframe v4, Oct 2026). **Implemented:** 2026-10-02 in `wwwroot/index.html` + `Fleet/InventoryEndpoints.cs`.

The rule applies here as everywhere (ENG-0016): nothing is shown that was not read live or computed
from live reads. Where the design wants a value we cannot read yet, the UI shows `—` or no icon, with
the reason in the tooltip. This list is what has to be built to fill those gaps.

## Needs building

| # | Design element | What happens today | What it takes |
|---|----------------|--------------------|---------------|
| 1 | **Service icons** `www` `dkr` `sql` `@` `ad` (e.g. "Web ports exposed: 80, 443 (via Coolify proxy)", "Database detected: MSSQL on 1433") | Shown **only** when a Proxmox tag on the guest states it (`web`, `docker`, `sql`, `mail`, `ad`…); the tooltip says it is stated, not detected. Untagged guests show no service icon. | A detector: TCP connect probes of well-known ports per guest IP (80/443, 25/587/993, 1433/3306/5432, 53/88/389), plus Docker/Coolify/Portainer detection (ports 2375/9443/8000 or the guest agent's process list). Needs a decision on scan cadence and on probing from the Coolify VM. |
| 2 | **SSD / HDD icons** on every disk | Real for sirius16 (direct disks, kernel rotational flag: HDD). **Unknown** on vega14, titan21, sagan25 and atlas19 — their disks sit behind PERC RAID virtual disks, which hide the media type. | Join to OpenManage: OME's physical-disk inventory has `MediaType` per drive and which virtual disk it belongs to. `OmeClient` already reads disks; it needs to keep media type and map VD → node block device. |
| 3 | **IP for every guest** | Live from the QEMU guest agent / LXC interfaces: **27 of 48** running guests on 2026-10-02. The other 21 say why: no agent configured (e.g. `arcserveprimary`, `i60dc2`, `gitlab40`), agent configured but not running in the guest (e.g. `aj-linux-box-71`, `coolify`, `dev-sql`), or agent timed out (`email124`). Stopped guests show `—`. | Not a code gap: install/enable `qemu-guest-agent` in those guests (and `agent: 1` where missing). Optional fallback worth building: ARP/DHCP lease lookup by MAC from the router — real data, but a different source. |
| 4 | **OS name** ("Ubuntu 22.04") | From the guest agent where it runs; otherwise from the config's `ostype` ("Windows (ostype win11)", "Linux"). | Same as #3 — agents. |
| 5 | **Clone…** | Menu item present, **disabled** ("not built yet"). | A clone verb: `POST /nodes/{n}/qemu/{vmid}/clone` (full/linked, target storage, new VMID/name), preflight + audit like Migrate. Needs `VM.Clone` added to the `VMentoryMigrate` role. |
| 6 | **Console** | Opens the Proxmox noVNC console in a new tab (`https://node:8006/?console=…`); the operator signs in to Proxmox there. | An embedded console needs a VNC ticket proxy (`vncproxy` + websocket relay through VMentory) — a real piece of work and a security decision. |
| 7 | **"Single cluster"** | The estate is 5 standalone nodes; the tree root is "Fleet" (overview) instead of a cluster. | Nothing — if the nodes are ever clustered, the root becomes the cluster. |
| 8 | **Real icons** | The design's text placeholders (`win`, `lnx`, `www`…) are used as-is, as the wireframe specifies ("placeholders for real icons"). | An icon set from the design system. |
| 9 | **Disk "used" inside the guest** ("620 GB · 310 GB used") | Shows **allocated on storage** (what the disk occupies on the pool, via the node probe) — labelled "allocated", not "used". | Guest-agent `get-fsinfo` gives filesystem used/total for agent-enabled guests; then both could be shown. |
| 10 | **Power actions tested end to end** | Start/Shutdown/Stop/Reboot are wired to Proxmox with the write token, state-checked, confirmed and audited; every guard rail tested. **No real power action has been sent** — that would touch a production guest. | First real test on a throwaway guest (same one as the first Migrate test). |

## Added after the design (owner request, 2026-10-02)

* **Activity strip** under the header — persistent across sessions and screens (server-side feed), shows every move / power action underway plus anything finished in the last 90 s; each item opens it.
* **Actions page** (`#actions`) — underway + recent moves, power actions and drains, each with live phase, task log, cancel / cleanup / abort. Executing a migration now lands here instead of a dialog.
* **Self-host hard block** — the VM that runs VMentory can only be moved live.

## Parked (built before v4, not in this design — backend still running, UI removed)

* Estate dashboard (machines × OpenManage hardware health) and the **remediation action tracker** (ENG-0015) — `/api/estate`, `/api/actions` still serve; the OME collector still raises actions.
* Fleet findings panels (boot safety, anti-affinity, pool spares, CPU groups) — `/api/fleet` still computes them. The v4 panel shows onboot=0 per guest and rules per guest only.
* **Clear host / drain plans**, maintenance mode, placement-rule editing — `/api/fleet/nodes/{id}/drain`, `/maintenance`, `/rules` still work; no UI.
* Scores and node ranking cards — still used inside **Migrate…** (ranked targets).
* Host add/edit/scan, VM table, cluster stats, export, legacy Hyper-V views (Phase 1/2.0 UI).

The previous UI is in git history at `b21ac22` (`wwwroot/index.html`).
