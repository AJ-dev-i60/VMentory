# Proxmox VE Integration

> Component spec · expands [ARCHITECTURE.md §Topology](../ARCHITECTURE.md#topology) (the Proxmox
> node row) and the `VMentory.Providers.Proxmox` project.
> Anchored to: Proxmox needs **no** agent — REST + SSH; and the **proven baseline is
> `qm importdisk` on the PVE node** (`virt-v2v` optional, not baseline — ENG-0001/0006). Secrets
> via `ISecretStore`, tokens-over-passwords (ENG-0002).

Unlike Hyper-V (Decision 1 → host agent, [agent-protocol.md](agent-protocol.md)), Proxmox ships a
first-class HTTP API and an SSH-reachable shell on every node, so `ProxmoxProvider` talks to it
**directly**. Two channels, deliberately: the **REST API (port 8006)** for orchestration and
stats, **SSH** for the disk/conversion operations the API can't express. Knowing which channel
owns which operation is the core of this spec.

---

## 1. Authentication

- **API tokens, not password tickets.** PVE supports `PVEAPIToken=USER@REALM!TOKENID=UUID` sent
  as an `Authorization` header — no login round-trip, no ticket/CSRF dance, and the token can be
  **privilege-separated** (scoped via ACLs to only the paths/actions we need). This is the
  ARCHITECTURE security intent: "PVE API tokens privilege-separated, not root ticket where
  avoidable" ([ARCHITECTURE.md §5](../ARCHITECTURE.md#5-security-this-is-now-a-hosted-service-not-loopback)).
- The token's secret is a **provider secret** — never persisted in the DB, held in `ISecretStore`
  (ENG-0002) and decrypted into memory only ([persistence-and-security.md §4](persistence-and-security.md#4-secret-handling--isecretstore--envelope-encryption-decided-eng-0002)).
  This is the **tokens-over-passwords** principle (ENG-0002) in practice: a scoped, revocable token,
  not a root password.
  The Phase-1 zero-on-dispose `Credentials` discipline ([Models.cs:121](../../../Models.cs#L121))
  carries forward to whatever holds the token in memory.
- **TLS:** PVE default certs are self-signed. Provider must support pinning the node's cert /
  CA fingerprint rather than disabling verification. See *Open question 1*.
- **SSH:** key-based auth to a dedicated account, ideally **forced-command-restricted** (ENG-0002);
  some operations (`qm importdisk`, `qemu-img`, optional `virt-v2v`) need root or `sudo`. SSH keys
  are secrets in `ISecretStore`, same handling as the API token. **Dedicated SSH key, not an SSH
  password** (tokens-over-passwords, ENG-0002).

---

## 2. REST surface used (by milestone)

Base: `https://{node}:8006/api2/json`. Read-only inventory/stats first
([ROADMAP.md §2.1](../ROADMAP.md)), writes and migration later.

| Endpoint | Method | Purpose | Milestone | Maps to |
|---|---|---|---|---|
| `/cluster/resources?type=vm` | GET | One-shot cluster-wide VM + node list (vmid, node, name, status, maxmem, maxcpu, maxdisk) | 2.1 | `GetVmsAsync` |
| `/nodes` · `/nodes/{n}/status` | GET | Host hardware/OS/uptime/RAM → `HostInfo` | 2.1 | `GetHostAsync` |
| `/nodes/{n}/qemu/{vmid}/status/current` | GET | Live per-VM state, mem, cpu, uptime | 2.1 | `GetVmStats` (live) |
| `/nodes/{n}/qemu/{vmid}/config` | GET | Firmware (`bios=ovmf\|seabios`), disks, NIC models, cores/sockets/mem | 2.1 / 2.3 | inventory + migration precheck |
| `/nodes/{n}/qemu/{vmid}/rrddata?timeframe=…` | GET | **Native time-series** (cpu/mem/disk/net) — Proxmox gives history for free, Hyper-V cannot | 2.1 | `GetStatsAsync` (historical) |
| `/nodes/{n}/storage` · `/storage/{s}/content` | GET | Storage pools + free space (precheck target capacity) | 2.3 | precheck |
| `/nodes/{n}/qemu/{vmid}/status/{start\|stop\|shutdown\|reset}` | POST | Lifecycle | 2.2 | `LifecycleAsync` |
| `/nodes/{n}/qemu/{vmid}/snapshot` | POST | Snapshot/checkpoint | 2.2 | `LifecycleAsync` |
| `/nodes/{n}/qemu` | POST | **Create VM shell** (vmid, cores, memory, bios, machine, net0…) | 2.3 | `CreateVmShellAsync` |
| `/nodes/{n}/qemu/{vmid}/config` | PUT/POST | Attach disk, set `boot`/`bootdisk`, `efidisk0`, NIC model | 2.3 | post-import wiring |
| `/nodes/{n}/qemu/{vmid}/migrate` | POST | Native same-platform (PVE↔PVE) migration | 2.4 | same-platform subgraph |
| `/nodes/{n}/tasks/{upid}/status` | GET | **Poll long-running task** (UPID) to completion | all writes | progress → SSE |

**The UPID pattern is fundamental.** Most PVE write/long operations return a task id (UPID)
immediately; you poll `tasks/{upid}/status` for `running`→`stopped` and `exitstatus`. This is the
Proxmox-side equivalent of Phase-1 fire-and-forget scan + progress broadcast
([Program.cs:297](../../../Program.cs#L297)): the provider polls the UPID and surfaces progress as
`IProgress<T>` → [EventHub.cs](../../../EventHub.cs) SSE
([provider-abstraction.md §2](provider-abstraction.md#2-the-interface)).

---

## 3. REST vs SSH — the boundary

| Operation | Channel | Why |
|---|---|---|
| Inventory, stats, lifecycle, snapshot | **REST** | First-class API endpoints exist. |
| Create VM shell, set config/boot/EFI | **REST** | `POST /qemu`, `PUT /config`. |
| Native PVE↔PVE migration | **REST** | `/migrate` endpoint. |
| `qm importdisk` — **the baseline** (VHDX→raw + attach) | **SSH** | The proven path (ENG-0001). No clean REST equivalent for importing an external disk image into a storage + attaching it. |
| `qemu-img info` (verify virtual size / no backing file) | **SSH** | Disk inspection on the node's filesystem. |
| `virt-v2v` — **optional** (Windows virtio injection) | **SSH** | CLI tool, runs on the node (or a conversion host — *Open question 2*). **Not the baseline.** |
| Reading the source VHDX onto the node (CIFS mount) | **SSH / node-local** | Bulk transfer, out-of-band; `qm importdisk` reads straight off the mount ([agent-protocol.md §6](agent-protocol.md#6-disk-transfer-is-out-of-band--a-proxmox-node-concern-eng-0001)). |

`ProxmoxProvider` therefore wraps **two clients**: a typed `HttpClient` (REST) and an SSH command
executor. The SSH executor is morally the same shape as Phase-1
[ReachabilityChecker.RunPowerShellAsync](../../../Reachability.cs#L155): run a command with a
timeout, **read stdout and stderr concurrently** to avoid pipe-buffer deadlock (the SSH library's
equivalent of CLAUDE.md gotcha 9), capture exit code. Reuse that discipline.

---

## 4. Gotchas

1. **Self-signed TLS by default** — plan for fingerprint pinning, don't ship "verify off".
2. **UPID polling, not synchronous returns** — treat every write as async-with-task-id (§2).
3. **`vmid` is cluster-unique but `node` is mutable** — a VM can be on a different node after a
   native migration. Don't cache `node` as identity; resolve it from `/cluster/resources`. This
   is the PVE side of the identity question in
   [provider-abstraction.md Open question 1](provider-abstraction.md#7-open-questions-need-a-human-decision).
4. **Storage types differ wildly** (`dir`, `lvm`, `lvmthin`, `zfspool`, `cephfs`) — disk import
   target and free-space precheck must be storage-type aware; `qm importdisk` behaviour and the
   resulting volume reference vary by backend.
5. **Firmware must match the source.** A Hyper-V Gen2 VM ([Models.cs:39](../../../Models.cs#L39))
   is UEFI → the PVE shell needs `bios=ovmf` **and** an `efidisk0`; Gen1 → `seabios`. Getting this
   wrong is the classic "imported VM won't boot." Owned by migration precheck/provision
   ([migration-job-model.md §3](migration-job-model.md#3-step-flow-hyper-v--proxmox-the-proven-path-eng-0001)).
6. **virtio drivers.** A Windows guest moved off Hyper-V won't boot from a virtio disk/NIC without
   drivers. The **baseline handles this in the guest-fix step** (attach SATA first, install
   virtio-win, switch to virtio-SCSI); **optional `virt-v2v`** can automate the injection. Don't
   attach the virtio bus and hope. See [migration-job-model.md §4](migration-job-model.md#4-disk-import-the-baseline--guest-fix--virt-v2v-is-optional-eng-0001).
7. **API token ACL scope** — a too-narrow token silently 403s on a path you forgot to grant;
   document the minimum ACL set per milestone so operators can scope tokens correctly.

---

## 5. Open questions (need a human decision)

1. **TLS trust model for PVE nodes.** Fingerprint pinning per node, an operator-supplied CA, or
   require proper certs? Affects onboarding UX. (Distinct from the agent PKI, which is decided —
   ENG-0005; PVE node trust is a separate, still-open question.) **Owner decision.**
2. **Where does the *optional* `virt-v2v`/`qemu-img` enhancement run** — on the PVE node over SSH
   (simplest, uses node CPU/disk) or a dedicated conversion container/host? The **baseline
   `qm importdisk` always runs on the PVE node** (ENG-0001), so this concerns only the optional
   conversion path. Shared with
   [ARCHITECTURE.md open questions](../ARCHITECTURE.md#open-questions) and
   [agent-protocol.md OQ 1](agent-protocol.md#9-open--resolved-questions).
3. **Cluster vs single-node addressing.** Do we register a cluster (one API entry point, resolve
   nodes dynamically) or individual nodes? `/cluster/resources` assumes a cluster; a lone node
   still answers it. Recommend registering a node and discovering the rest — confirm.

> **Deploy/Backup note (ENG-0006):** when those pillars land, `ProxmoxProvider` gains create-from-
> template / install-from-ISO and snapshot/export/restore surfaces (native PVE backup, PBS
> integration). The **backup buy-vs-build approach is deferred** (ENG-0006) — do not assume a PVE
> backup mechanism here yet.
