# Migration Job Model

> Component spec · expands [ARCHITECTURE.md §4 Operations engine](../ARCHITECTURE.md#4-operations-engine-generalized-from-the-migration-engine--eng-0006).
> **Decided:** the **proven path is `qm importdisk` on the PVE node** (ENG-0001) — **virt-v2v is
> optional**, not the baseline (ENG-0001/0006); the Hyper-V side is driven by the **agent**, not
> winrun.py (ENG-0001); migration is **bidirectional in the end state, HV→PVE first** (ENG-0006).
> The migration DAG is the first instance of the **general operations engine** (deploy/backup/
> restore reuse it, ENG-0006). Delivered in [ROADMAP.md §2.3](../ROADMAP.md). Grounded in the
> validated [`migrate-vm`](../../../migrate-vm/SKILL.md) skill.

A migration is a **persisted, resumable DAG of steps**. The engine's job is orchestration,
sequencing, idempotency, logging, and rollback — **not** disk conversion. The mechanical work is
delegated to **proven tools** invoked through the providers
([provider-abstraction.md §2](provider-abstraction.md#2-the-interface)) over their respective
channels ([agent-protocol.md](agent-protocol.md), [proxmox-integration.md §3](proxmox-integration.md#3-rest-vs-ssh--the-boundary)).

> **This same engine is the general operations engine (ENG-0006):** migrate, deploy, backup, and
> restore are all persisted, resumable, dry-runnable jobs on this machinery. This spec defines the
> migration step graph; the other pillars add their own step graphs.

---

## 1. Why a persisted DAG

A migration runs for minutes to hours, touches two platforms, and must survive a Core restart
mid-flight without orphaning a half-created VM or a half-copied 200 GB disk. Phase-1 state is
in-memory and ephemeral ([Store.cs:5](../../../Store.cs#L5) "no disk writes, no persistence") —
that model cannot back a long migration. So:

- **Job + Step + Log rows are persisted** ([persistence-and-security.md §1](persistence-and-security.md#1-schema-sketch)).
- Each **step is a node** with declared **dependencies** (the DAG), a status
  (`Pending`/`Running`/`Succeeded`/`Failed`/`Skipped`/`RolledBack`), inputs, and outputs.
- On restart, the engine reloads the job and resumes from the first non-`Succeeded` step whose
  dependencies are satisfied — hence every step must be **idempotent** and **resumable** (§7).

DAG, not a flat list, because same-platform migration is "a *simpler* subgraph of the same engine"
([ARCHITECTURE.md §4](../ARCHITECTURE.md#4-operations-engine-generalized-from-the-migration-engine--eng-0006)) — HV↔HV / PVE↔PVE
([ROADMAP.md §2.4](../ROADMAP.md)) reuse the import/transfer nodes by pruning them, and batch
migrations ([ROADMAP.md §2.4](../ROADMAP.md)) compose multiple per-VM subgraphs.

**Resumability spans the agent too (ENG-0004):** because the agent's verbs are idempotent/resumable,
a step survives an **agent** restart mid-job, not only a Core restart. Both ends reload and continue.

---

## 2. Job lifecycle & modes

- **Dry-run** ([ARCHITECTURE.md §4](../ARCHITECTURE.md#4-operations-engine-generalized-from-the-migration-engine--eng-0006), ROADMAP 2.3 exit
  bar): execute Precheck + plan every step, mutate nothing on either platform. Required before any
  real run in the wizard.
- **Run**: execute steps in dependency order, streaming progress.
- **Stop**: hard stop + rollback hooks (§7). Every step takes a `CancellationToken` — the engine
  threads CTs end-to-end exactly as Phase 1 does
  ([Reachability.cs:155](../../../Reachability.cs#L155), [Scanner.cs:56](../../../Scanner.cs#L56)).
- **Resume**: after crash/restart, reload and continue.

---

## 3. Step flow: Hyper-V → Proxmox (the proven path, ENG-0001)

The reference flow follows the validated [`migrate-vm`](../../../migrate-vm/SKILL.md) skill,
expanded with the owning channel and idempotency note for each step. The HV side is driven by the
**agent** (ENG-0001 — not winrun.py); disk movement and import are **Proxmox-node** concerns.

| # | Step | Channel / tool | Idempotency key | Rollback |
|---|---|---|---|---|
| 1 | **Precheck** | both providers' `Capabilities` + REST/agent reads | read-only | none needed |
| 2 | **Resolve checkpoints** | agent `ResolveCheckpoints` (merge `.avhdx` → flat `.vhdx`) | disk already flat (no backing file) → skip | none (merge is forward-only; non-destructive of data) |
| 3 | **Quiesce** | agent `Quiesce` — shutdown or checkpoint **by operator choice** (§ below, ENG-0006) | record prior power state | restart source to prior state |
| 4 | **Expose + transfer disk** | agent `LocateDisks`/`ExposeDisk` (locator only) + **PVE node CIFS mount** ([proxmox-integration.md §3](proxmox-integration.md#3-rest-vs-ssh--the-boundary)); disk transfer is a Proxmox-node concern ([agent-protocol.md §6](agent-protocol.md#6-disk-transfer-is-out-of-band--a-proxmox-node-concern-eng-0001)) | flat VHDX reachable on the node | unmount; delete any staged artifacts |
| 5 | **Provision** | PVE REST `POST /nodes/{n}/qemu` ([proxmox-integration.md §2](proxmox-integration.md#2-rest-surface-used-by-milestone)) — match vCPU/RAM/firmware, preserve MAC | vmid exists with our marker → reuse | destroy created shell |
| 6 | **Import disk** | **`qm importdisk` on the PVE node (SSH)** — VHDX→raw — + `PUT /config` boot/NIC/EFI (REST) | disk already attached to vmid → skip | detach + delete imported volume |
| 7 | **Guest fix** | agent/node guest customization — Linux netplan match-by-MAC; Windows virtio/SATA driver handling (§4) | fix marker present in guest/config → skip | revert the targeted edit |
| 8 | **First boot + validate (safe)** | PVE REST `status/start` **with link down**, console check, then prove source-off before link-up | boot-validated flag | stop the VM |
| 9 | **Cutover** | flip DNS/notes, mark source decommissioned — **never auto-delete source** | cutover-done flag | revert notes/DNS; source is still intact |

**Precheck (step 1) detail** — the gate that prevents doomed runs:
capability match (source disk-export, target disk-import + create-shell,
[provider-abstraction.md §3](provider-abstraction.md#3-capability-model)); target storage free
space (`/nodes/{n}/storage`, [proxmox-integration.md §2](proxmox-integration.md#2-rest-surface-used-by-milestone));
guest OS class (Linux vs Windows → drives the guest-fix path, §4); **firmware mapping** Gen2→OVMF
([Models.cs:39](../../../Models.cs#L39) `Generation` → PVE `bios=ovmf`+`efidisk0`,
[proxmox-integration.md gotcha 5](proxmox-integration.md#4-gotchas)); current power state and
**checkpoint state** (an active `.avhdx` means step 2 must run first — copying a differencing disk
alone yields a broken image).

**Quiesce by operator choice (step 3, ENG-0006):** the wizard offers shutdown vs checkpoint/live
**per job, with explicit caveats** — static frontend → favor uptime; database server → favor
consistency/graceful shutdown. **Not hardcoded.** For Linux guests, prefer an *in-guest*
`shutdown -h now` — Hyper-V's graceful `Stop-VM` relies on the LIS daemon and often silently no-ops
on Linux, leaving the source running (which later masquerades as a "successful" migration).

**Safe verification (step 8) is load-bearing — the skill's two rules.** The copy is built with the
**same MAC and IP** as the source, so ping/ARP **cannot** distinguish them. Boot the copy
**isolated** (`link_down=1`), confirm via the **console**, and **prove the source is off** (its IP
goes dark) before bringing the copy onto the network. Never run both on the same L2 segment at once.

**Cutover safety is load-bearing:** the source is left intact (shut down, **auto-start disabled**)
so rollback is always possible. Decommissioning the source is a separate, explicit, later operator
action — not a migration step.

---

## 4. Disk import (the baseline) + guest fix — virt-v2v is optional (ENG-0001)

**The proven, baseline path is `qm importdisk` on the PVE node** (ENG-0001): it reads the flat VHDX
off the CIFS mount and converts **VHDX→raw** as it imports onto target storage. No separate
`qemu-img convert` step, and **no virt-v2v in the baseline**. The skill validated exactly this
(Ubuntu 22.04 Gen2, 2026-06-12).

```
qm importdisk <vmid> "/mnt/import/<path>/<disk>.vhdx" <storage>   # VHDX → raw, onto target storage
qm set <vmid> --scsi0 <storage>:vm-<vmid>-disk-N,iothread=1,discard=on --boot order=scsi0
```

Because a freshly-imported disk has **no virtio drivers in the guest**, the **guest-fix step**
(step 7) makes it actually boot:

- **Linux guest:** the trap is the NIC-name mismatch — the netplan config names `eth0` (Hyper-V
  netvsc) but the virtio NIC comes up as `ens18`, so boot **hangs on "Wait for Network to be
  Configured."** Fix deterministically from the PVE node (VM stopped): mount the guest root
  (usually **LVM** inside the partition — activate the guest VG) and rewrite netplan to **match by
  MAC** (the MAC is preserved, so this is stable). `scripts/fix-guest-netplan.sh` is the reference.
- **Windows guest:** the analogue is the **virtio storage driver** — a virtio-SCSI boot disk with
  no driver BSODs `INACCESSIBLE_BOOT_DEVICE`. Either pre-install virtio-win in the guest *before*
  shutdown, or attach the boot disk on **SATA first**, install virtio-win inside Windows, then
  switch to virtio-SCSI for performance.

**virt-v2v is an OPTIONAL enhancement, not the baseline (ENG-0001/0006):** it can automate the
Windows **virtio driver injection** so the SATA-first dance isn't needed. When used, the engine
**wraps** the tool (builds argv, runs it over the SSH executor with a timeout, reads stdout/stderr
concurrently — same anti-deadlock discipline as [Reachability.cs:179](../../../Reachability.cs#L179),
tails output as the step log §6, maps exit code → success); it does **not** reimplement conversion
internals. Detect guest OS from inventory ([Scanner.cs:289](../../../Scanner.cs#L289) `GuestOs`).

Where the *optional* conversion enhancement runs (PVE node vs dedicated conversion host) is the
still-open conversion-host-placement question (§8) — the **baseline `qm importdisk` always runs on
the PVE node**, so this affects only the optional path.

---

## 5. Edge cases the engine must handle

- **UEFI / Gen2 → OVMF:** provision `bios=ovmf` + `efidisk0`; without the EFI disk the VM won't
  boot. Precheck asserts it, Provision/Attach set it.
- **Secure Boot:** explicitly a 2.4 edge case ([ROADMAP.md §2.4](../ROADMAP.md)) — OVMF Secure
  Boot keys don't transfer; the skill provisions the EFI disk **without pre-enrolled keys**
  (`pre-enrolled-keys=0`, Secure Boot off, avoids bootloader rejection). Document as "migrate with
  Secure Boot off, re-enable post-migration" until handled. See *Open question 2*.
- **virtio vs IDE/SATA boot:** the **baseline** handles this via the guest-fix step (§4) — attach on
  an emulated bus (sata/ide) first, switch to virtio after the guest has the driver. Optional
  `virt-v2v` removes the need by injecting drivers, but the baseline SATA-first path is the default.
- **Generation-1 / BIOS guests:** straightforward `seabios`, no EFI disk.
- **Multiple VHDs per VM:** Phase-1 inventory already models a list of disks
  ([Models.cs:44](../../../Models.cs#L44) `List<Vhd>`); expose/import fan out per disk and
  rejoin — each disk is its own idempotency unit.
- **Dynamic memory / generation-specific fields:** Hyper-V `DynamicMemory`
  ([Models.cs:43](../../../Models.cs#L43)) has no exact PVE equivalent — map to ballooning min/max
  and warn on imperfect fidelity rather than failing.

---

## 6. Per-step logging & progress

Each step writes **structured, persisted logs** ([persistence-and-security.md §1](persistence-and-security.md#1-schema-sketch))
— survives restart, available for post-mortem. Live progress streams over SSE by reusing
[EventHub.cs](../../../EventHub.cs) exactly as Phase-1 scan progress does
([Program.cs:339](../../../Program.cs#L339) `scanProgress`, [Program.cs:405](../../../Program.cs#L405)
`scanComplete`): provider `IProgress<T>`
([provider-abstraction.md §2](provider-abstraction.md#2-the-interface)) → Web → `Broadcast`. The
migration wizard ([ROADMAP.md §2.3](../ROADMAP.md)) consumes the same stream. `qm importdisk`
progress (and optional `virt-v2v` output) arrives via the agent/SSH streaming paths
([agent-protocol.md §5](agent-protocol.md#5-streaming-progress)).

---

## 7. Stop, rollback, and idempotency

- **Idempotent steps.** Each step checks "is my output already present?" before acting (the
  Idempotency-key column in §3) so a resumed job re-running a step is a no-op, not a duplicate.
  This is what makes resume-after-crash safe.
- **Rollback hooks.** Each step declares a compensating action (§3 Rollback column). A hard stop
  or a step failure unwinds completed steps in reverse dependency order. Because cutover never
  deletes the source, full rollback always lands back on a working source VM.
- **Resumable across both ends.** Engine state lives in the DB, not memory — a Core restart reloads
  and continues; and because the agent's verbs are idempotent/resumable (ENG-0004), a step survives
  an **agent** restart mid-job too. The explicit engine requirement: "each step idempotent and
  resumable, structured per-step logs persisted, hard stop + rollback hooks, dry-run mode"
  ([ARCHITECTURE.md §4](../ARCHITECTURE.md#4-operations-engine-generalized-from-the-migration-engine--eng-0006)).
- **Audit.** Every migration is a write action → recorded in the audit log; the agent also logs each
  executed verb back to Core (ENG-0004) ([persistence-and-security.md §6](persistence-and-security.md#6-audit-log),
  [ROADMAP.md cross-cutting](../ROADMAP.md#cross-cutting-every-milestone)).

---

## 8. Open / resolved questions

**Resolved (do not re-open):**
- ~~Baseline conversion = virt-v2v?~~ → **No. Baseline is `qm importdisk` on the PVE node**
  (VHDX→raw); **virt-v2v is optional** for Windows virtio injection (ENG-0001/0006).
- ~~HV side driven by winrun.py?~~ → **No — driven by the agent** (ENG-0001); winrun.py is
  behavioral reference only.
- ~~Quiesce default~~ → **operator choice per job, with caveats** (ENG-0006) — not a single
  hardcoded default.
- ~~Migration directionality~~ → **bidirectional end-state, HV→PVE first**; PVE→HV is a distinct
  later effort (ENG-0006).

**Still open (need a human decision; the spec proceeds against the baseline path):**
1. **Conversion-host placement** (for the *optional* `virt-v2v`/`qemu-img` enhancement only) — PVE
   node over SSH vs a dedicated conversion container. The **baseline `qm importdisk` runs on the PVE
   node** regardless. Shared with
   [ARCHITECTURE.md open questions](../ARCHITECTURE.md#open-questions),
   [proxmox-integration.md OQ](proxmox-integration.md#5-open-questions-need-a-human-decision),
   [agent-protocol.md OQ](agent-protocol.md#9-open--resolved-questions). **Owner decision.**
2. **Secure Boot guests in MVP scope.** Confirm 2.3 MVP may *exclude* Secure-Boot Windows guests
   (precheck rejects them clearly) and defer to 2.4, rather than attempting key migration.
   **Owner decision.**
3. **Guest-customization engine** (ENG-0006, shared with Deploy) — cloud-init/sysprep/unattend vs
   platform-native for the guest-fix step (§4) as it generalizes beyond the netplan/virtio cases.
   A future ENG topic.
4. **Stable VM identity across migration** — provenance keying for source/target
   ([provider-abstraction.md OQ](provider-abstraction.md#7-open-questions-need-a-human-decision)).
