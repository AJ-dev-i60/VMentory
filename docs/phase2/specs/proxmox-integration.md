# Proxmox VE Integration

> Component spec · expands [ARCHITECTURE.md §Topology](../ARCHITECTURE.md#topology) (the Proxmox
> node row) and the `VMentory.Providers.Proxmox` project.
> Anchored to **Decision 1** (Proxmox needs **no** agent — REST + SSH) and **Decision 3**
> (orchestrate `qm`/`qemu-img`/`virt-v2v`, don't rebuild conversion).

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
- The token's secret is a **provider secret** — never persisted in the DB, comes from the secret
  store and is decrypted into memory only ([persistence-and-security.md §4](persistence-and-security.md#4-secret-handling)).
  The Phase-1 zero-on-dispose `Credentials` discipline ([Models.cs:121](../../../Models.cs#L121))
  carries forward to whatever holds the token in memory.
- **TLS:** PVE default certs are self-signed. Provider must support pinning the node's cert /
  CA fingerprint rather than disabling verification. See *Open question 1*.
- **SSH:** key-based auth to a dedicated low-privilege account where possible; some operations
  (`qm`, `qemu-img`, `virt-v2v`) need root or `sudo`. SSH keys are secrets, same handling as the
  API token.

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
| `qm importdisk` (attach a converted raw/qcow2) | **SSH** | No clean REST equivalent for importing an external disk image into a storage + attaching it. |
| `qemu-img convert` / `qemu-img info` | **SSH** | Disk-format work happens on the node's filesystem. |
| `virt-v2v` (Windows guest virtio injection) | **SSH** | CLI tool, runs on the node (or a conversion host — *Open question 2*). |
| Placing the source disk image onto node storage | **SSH/scp** or staging | Bulk transfer, out-of-band ([agent-protocol.md §6](agent-protocol.md#6-disk-transfer-is-out-of-band)). |

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
   ([migration-job-model.md §3](migration-job-model.md#3-step-flow-hyper-v--proxmox)).
6. **virtio drivers.** A Windows guest moved off Hyper-V won't boot from a virtio disk/NIC without
   drivers — this is exactly why Decision 3 prefers `virt-v2v` (it injects them). Don't attach
   virtio bus and hope. See [migration-job-model.md §4](migration-job-model.md#4-conversion-virt-v2v-wrapping).
7. **API token ACL scope** — a too-narrow token silently 403s on a path you forgot to grant;
   document the minimum ACL set per milestone so operators can scope tokens correctly.

---

## 5. Open questions (need a human decision)

1. **TLS trust model for PVE nodes.** Fingerprint pinning per node, an operator-supplied CA, or
   require proper certs? Affects onboarding UX. **Owner decision.**
2. **Where does `virt-v2v`/`qemu-img` run** — on the PVE node over SSH (simplest, uses node CPU
   and disk) or a dedicated conversion container/host? This is the open ARCHITECTURE question
   ([ARCHITECTURE.md open questions](../ARCHITECTURE.md#open-questions-for-the-spec-agents)) and
   it sets whether SSH-to-node is on the migration hot path or just orchestration. Tied to
   [agent-protocol.md Open question 2](agent-protocol.md#7-open-questions-need-a-human-decision).
3. **Cluster vs single-node addressing.** Do we register a cluster (one API entry point, resolve
   nodes dynamically) or individual nodes? `/cluster/resources` assumes a cluster; a lone node
   still answers it. Recommend registering a node and discovering the rest — confirm.
