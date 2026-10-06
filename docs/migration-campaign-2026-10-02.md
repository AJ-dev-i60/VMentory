# Migration test campaign — 2026-10-02

The owner named nine low-importance VMs that may be shut down and moved freely (ome200, isxdc1, isxdc2,
iis104, i60dc2, wazuh131, wazuh132, arcserveprimary, lifetime137) and asked for the move path to be made
robust, trustworthy and reliable. Every test drove **VMentory's own move path** (a local instance of this
code against the five real nodes, separate database), with outside probes and ground truth read from the
nodes as root. No numbers below are estimates unless they say so.

## Results

| # | Scenario | Guest / route | Result | What it showed |
|---|----------|---------------|--------|----------------|
| T1 | Offline, across CPU groups, Linux with agent, IDE cloud-init drive | wazuh132 sagan25 → atlas19 (19.7 GB) | ✅ 3 min copy, **108.7 MB/s**, 235 s outage seen from outside | The offline cloud-init "blocker" from the brief was wrong for offline moves — Proxmox regenerates the drive. Narrowed to live moves. |
| T2a | Offline Windows, no agent | i60dc2 titan21 → atlas19 | ⛔ clean failure (by design) | Windows ignored the ACPI button; the guest kept running, untouched. This is the owner's original "failed migration of i60dc2". |
| T2b | Same, force-off allowed | i60dc2 | ❌ failed **after a full 6.5 min copy** | Target's final `config` step: `403 Permission check failed (/, Sys.Modify)` — the guest has `startup: order=1`, which Proxmox treats as host-level. Proxmox cleaned up, runner restarted the guest on the source. |
| T2c | Same, after the fixes | i60dc2 | ✅ 6 min copy, ETA predicted 370 s vs 371 s actual, DNS outage 514 s | Second ACPI press honoured; DC served DNS again from the new host (different CPU generation). |
| T3 | **Live**, same CPU model, shadow workload | isxdc1 sirius16 → vega14 (60 GB virtual) | ✅ 4 min 42 s | In-guest writer (18 lines/s): **no lines lost**, longest in-guest gap < 0.4 s. Outside, every 100 ms: DNS 7/3556 failed, LDAP 6/3556, never two in a row; one ~1 s stall at switchover. Ping: 14/1800 lost, one 2.3 s gap (network relearning the MAC). Fixed the iSixty DC anti-affinity breach as a side effect. |
| T4 | Live across CPU groups | wazuh132 → titan21 | ✅ refused | "different CPU models … would fail or crash the guest". |
| T5 | Cancel mid-copy | wazuh131 titan21 → atlas19 | ✅ after fixes | First run: cleanup claimed success but removed nothing; a `@__migration__` snapshot left on the source made the **next move fail in 16 s**. After fixes: residue report matched ground truth item for item; next preflight blocked with the exact command. |
| T6 | VMentory killed 60 s into a 94 GB copy, down 60 s, restarted | wazuh131 titan21 → atlas19 | ✅ 108.6 MB/s | Proxmox kept copying; the runner re-attached by UPID and finished the move. |
| T7 | Move with "remove the source copy on success" | ome200 atlas19 → titan21 (35.6 GB) | ✅ 5 min 49 s, 107.9 MB/s | Nothing left on atlas19 — no config, no volume, no snapshot. |
| T8+T9 | Two moves queued at the same instant | iis104 titan21 → sagan25 (83.7 GB), arcserveprimary sagan25 → atlas19 (111 GB) | ✅ never overlapped; 105.6 and 114.3 MB/s; iis104 copy 842 s vs ETA 813 s (+3.5 %) | iis104: agent configured but **not running** — retry logic judged by "configured", skipped the second ACPI press → fell to the operator-allowed force-off. Fixed. arcserveprimary (pinned, iSCSI LUN) shut down cleanly on the **second** ACPI press; source copy stopped + locked — it can never run twice. |
| T10 | Long copy to a non-default pool | lifetime137 titan21 → atlas19 `tank` (826 GB allocated) | ✅ copy 2 h 02 m 33 s (15:14:52 → 17:17:25), ETA 7532 s vs 7353 s actual (−2.4 %) | Ignored **both** ACPI presses → operator-allowed force-off. **The guest then stayed stopped on atlas19 until 2026-10-05 10:49** — the laptop driving the move was switched off, and the handover worker was launched without `--perm auto` and stalled on permission prompts, so nothing started it. Not a VMentory defect, but the reason a move must be driven by the deployed instance, never a laptop. Source copy on titan21 stopped + `lock: migrate`; no `@__migration__` snapshot left. |

### Windows guests without a working agent

Of the four tested, two honoured the **second** ACPI press (i60dc2, arcserveprimary) and two ignored both
(iis104, lifetime137). The automatic second press recovers half; for the rest the per-move "force it off"
opt-in is the only reliable path — or install/start the QEMU guest agent in those guests (21 of 48
running guests have no working agent today).

## Fixes made from the findings

1. **Write-token privileges checked live in preflight** against what *this* guest needs; `Sys.Modify` added to
   the `VMentoryMigrate` role (needed for guests with `startup:`). A move can no longer die at its last step
   over a privilege.
2. **Shutdown:** a second ACPI press before giving up; an explicit, per-move opt-in to force a power-off;
   the shutdown path is judged by whether the agent *answers*, not whether it is configured.
3. **Residue of interrupted moves:** the probe reports `@__migration__` snapshots; preflight blocks with the
   exact removal command; after a cancel/failure the runner re-probes both nodes and records precisely what
   was left behind (snapshot, locked placeholder, orphaned volume) and how to remove it.
4. **Cleanup tells the truth:** Proxmox deletes asynchronously; cleanup now waits for the destroy task and
   reports its real result (a locked placeholder needs root — the exact commands are given).
5. **Progress:** offline copies log no progress in Proxmox; the phase shows elapsed time and an estimate from
   the measured rate, labelled as an estimate. Live moves show Proxmox's real "transferred X of Y".
6. **Rates:** live and offline moves have separate measured rates (a live move walks the whole virtual disk
   and copies RAM). The offline ETA was within 1 s on a 6-minute copy.
7. **Store choice:** thin-capable stores (ZFS, LVM-thin) before directory stores — "most free space" had sent
   a domain controller to vega14's `dblogs` SQL-log store.
8. **One-click ISO eject** for the commonest preflight blocker.
9. **Opt-in "remove the source copy on success"** (`delete=1`): clearing a migrate lock later is root-only,
   so this is the only non-root way not to accumulate locked copies.
10. **Self-host hard block** (earlier today): the VM that runs VMentory can only move live.
11. **OLD COPY label:** the stopped, migrate-locked copy a move leaves on the old node is dimmed and tagged in
    the tree, so it cannot be mistaken for the live guest.

## Still not covered / known limits

* Containers (LXC restart mode) — none of the named guests is a container.
* Cancelling an offline copy always leaves residue that only root can remove (Proxmox limitation); VMentory
  now names it exactly.
* Old source copies from moves made without "remove source copy" stay locked until removed as root.

## Cleanup — 2026-10-06

All eight migrate-locked source copies left by the campaign (isxdc1 on sirius16; iis104, i60dc2, wazuh131,
lifetime137 on titan21; arcserveprimary, wazuh132, ome200 on sagan25) were removed as root after checking each
was stopped, `lock: migrate`, and its guest running live on another node. ~933 GiB (on-disk) freed; no configs,
volumes or snapshots left. Every moved guest still running afterwards.
