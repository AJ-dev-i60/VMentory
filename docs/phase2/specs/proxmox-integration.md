# Proxmox VE Integration

> Component spec · expands [ARCHITECTURE.md §Topology](../ARCHITECTURE.md#topology) (the Proxmox
> node row) and the `VMentory.Providers.Proxmox` project.
> **Decided (ENG-0009, 2026-06-16):** Proxmox deep actions are driven by the **native PVE REST API
> as the primary control plane** + a **constrained, forced-command, dedicated-account SSH key** for
> the disk-import / conversion / on-node guest-edit residue. **No agent is installed on Proxmox
> nodes** — onboarding a node is "paste a scoped API token + an SSH key," both held in `ISecretStore`
> and revocable. The **proven migration baseline is `qm importdisk` on the PVE node** (`virt-v2v`
> optional, not baseline — ENG-0001/0006). Secrets via `ISecretStore`, tokens-over-passwords
> (ENG-0002).

Unlike Hyper-V — which is a black box from a Linux container and so reaches the host through an
installed agent ([agent-protocol.md](agent-protocol.md), scoped to the HV migration source per
ENG-0009) — Proxmox ships a first-class HTTPS API (port 8006) and an SSH-reachable shell on every
node. It is **not** a black box that needs an agent to become reachable, so `ProxmoxProvider` talks
to it **directly with no installed software on the node** (ENG-0009). Two channels, deliberately and
asymmetrically: the **REST API is the primary control plane** for orchestration, stats, lifecycle,
provisioning and native migration; **SSH is the bounded residue channel** for the four disk/guest
operations the API cannot express (§3). Knowing which channel owns which operation is the core of
this spec.

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
  The Phase-1 zero-on-dispose `Credentials` discipline ([Models.cs:121](../../../VMentory.Core/Models.cs#L121))
  carries forward to whatever holds the token in memory.
- **Structured token storage — decompose at rest, recompose bare at the seam (ENG-0012, planned).**
  Under the named-credential model ([persistence-and-security.md §4a](persistence-and-security.md#4a-named-credentials--first-class-reusable-entities-decided-eng-0012-planned)),
  a PVE token is a `ProxmoxToken` credential: the **non-secret** `user@realm` + `tokenid` live in the
  `credential.Descriptor` JSON (columns/metadata, no decryption to read), and **only the secret UUID**
  lives in `ISecretStore` under `cred:{Id}:token`. The provider **recomposes the bare**
  `user@realm!tokenid=secret` string at `ProxmoxProvider.BuildClient` exactly as today — still sent via
  `TryAddWithoutValidation` (CLAUDE.md gotcha #13). **Storage is structured; the wire format is
  unchanged.** This is also what kills the connection-string-paste confusion that triggered ENG-0012:
  the UI collects three discrete fields that map directly onto the descriptor + its vault slot, instead
  of one opaque compound string. The Phase-1 "bare token in a single per-host `host_cred:{hostId}` blob"
  is **superseded-by-ENG-0012**; the C1 promotion decomposes existing bare tokens on first startup
  ([persistence-and-security.md §4a](persistence-and-security.md#c1--global-credentials-retired--the-promotion-migration)).
- **TLS:** PVE default certs are self-signed. Provider must support pinning the node's cert /
  CA fingerprint rather than disabling verification. See *Open question 1*.
- **SSH hardening posture (ENG-0009 + ENG-0002).** SSH is a broad-privilege channel and must be
  constrained — it is the residue channel, not a general node shell:
  - **Dedicated account, not root login** where feasible; the few root operations
    (`qm importdisk`, guest-root mount/edit) go via `sudo` for the specific commands, not a blanket
    root key. (Account-with-sudo vs root-only forced-command key is *Open question 4*.)
  - **Forced-command-restricted key** (`command=` in `authorized_keys`, plus `no-port-forwarding`,
    `no-X11-forwarding`, `no-agent-forwarding`, `no-pty`) so the key can run only the bounded residue
    command set (§3), never an interactive shell.
  - **Key, not a password** (tokens-over-passwords, ENG-0002). The private key is a secret in
    `ISecretStore`, same handling as the API token, and is **revocable** (drop it from the node's
    `authorized_keys`). Under ENG-0012 (planned) it is a `ProxmoxSshKey` credential in the host's
    **`TransportCredentialId`** slot (distinct from the API-token `ManagementCredentialId` slot — the D2
    two-slot model exists precisely so Proxmox can hold both secrets at once); the private key + optional
    passphrase are two vault slots (`cred:{Id}:sshkey` + `cred:{Id}:passphrase`) under one credential —
    [persistence-and-security.md §4a](persistence-and-security.md#a-credential-owns-a-set-of-named-secret-keys--credidslot).
  - The SSH command set is exactly the validated `migrate-vm` skill's — **bounded and
    well-understood**, not an open-ended subsystem (ENG-0009).

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

**The boundary (ENG-0009):** REST is the **primary control plane** for orchestration + stats +
lifecycle + provisioning + native migration; SSH is the **bounded residue channel** for exactly four
operations the REST API cannot express. Both are node-native; **neither requires installing software
on the node** (no agent — ENG-0009).

**REST owns everything it covers:**

| Operation | Channel | Why |
|---|---|---|
| Inventory, stats, lifecycle, snapshot | **REST** | First-class API endpoints exist (§2). |
| Create VM shell, set config/boot/EFI | **REST** | `POST /qemu`, `PUT /config` (§2). |
| Native PVE↔PVE migration | **REST** | `/migrate` endpoint (§2). |
| Long-op progress | **REST** | every write returns a UPID; poll `tasks/{upid}/status` (§2). |

**SSH owns exactly these four residue items — and only these:**

| # | SSH-only operation | Why no REST equivalent |
|---|---|---|
| 1 | **`qm importdisk`** — the migration baseline (VHDX→raw + attach into a storage) | The proven path (ENG-0001/0009). **No clean REST endpoint** for importing an *external* disk image into a storage and attaching it. *Open question 5* tracks verifying this against a live PVE in case a newer release adds one. |
| 2 | **`qemu-img info`** — disk inspection (verify virtual size, no backing file) | Reads a file on the node's filesystem; not an API resource. |
| 3 | **Reading the source disk onto the node** (CIFS mount + node-local bulk transfer) | Out-of-band bulk byte movement, not a control-channel operation; `qm importdisk` reads straight off the mount ([agent-protocol.md §6](agent-protocol.md#6-disk-transfer-is-out-of-band--a-proxmox-node-concern-eng-0001)). |
| 4 | **On-node guest-root edit** — mount the guest filesystem on the PVE node and edit it (the skill's match-by-MAC netplan fix: activate guest VG, edit, detach) | A node-shell filesystem operation by nature; no API surface mounts and edits a guest's root. |

> **Optional, *not* part of the SSH residue baseline:** `virt-v2v` (Windows virtio injection) is a
> CLI enhancement that runs on the node or a dedicated conversion host (*Open question 2*) — it is
> **not** the baseline and not one of the four required residue items above.

Everything outside those four items is REST. If a future deep action seems to "need SSH," check it
against this list first — the validated `migrate-vm` skill proves these four (plus the optional
`virt-v2v`) are the *entire* SSH surface; nothing else on the node is driven over SSH (ENG-0009).

`ProxmoxProvider` therefore wraps **two clients**: a typed `HttpClient` (REST, the primary control
plane) and a constrained SSH command executor (the four-item residue channel, §1 hardening). The SSH
executor is morally the same shape as Phase-1
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
5. **Firmware must match the source.** A Hyper-V Gen2 VM ([Models.cs:39](../../../VMentory.Core/Models.cs#L39))
   is UEFI → the PVE shell needs `bios=ovmf` **and** an `efidisk0`; Gen1 → `seabios`. Getting this
   wrong is the classic "imported VM won't boot." Owned by migration precheck/provision
   ([migration-job-model.md §3](migration-job-model.md#3-step-flow-hyper-v--proxmox-the-proven-path-eng-0001)).
6. **virtio drivers.** A Windows guest moved off Hyper-V won't boot from a virtio disk/NIC without
   drivers. The **baseline handles this in the guest-fix step** (attach SATA first, install
   virtio-win, switch to virtio-SCSI); **optional `virt-v2v`** can automate the injection. Don't
   attach the virtio bus and hope. See [migration-job-model.md §4](migration-job-model.md#4-disk-import-the-baseline--guest-fix--virt-v2v-is-optional-eng-0001).
7. **API token ACL scope** — a too-narrow privilege-separated token silently 403s on a path you
   forgot to grant; document the minimum ACL set per milestone so operators can scope tokens
   correctly. Enumerating that set is *Open question 5*.
8. **Split PVE 401 vs 403 server-side (ENG-0011a, planned).** Today both collapse into one "API token
   rejected" string ([ProxmoxProvider.cs:45,92](../../../ProxmoxProvider.cs#L45)). ENG-0011a requires the
   `catch … when (401 || 403)` filter be split into two arms: **401 → `AUTH_REJECTED`** (tier
   `Unauthorized`, "wrong realm/token-id" — fix the credential) vs **403 → `AUTH_INSUFFICIENT_PRIV`**
   (tier `Degraded`, valid-but-unprivileged — grant the role). This is exactly the gotcha-#13 distinction,
   finally surfaced. A post-connect scan error emits `SCAN_FAILED` (Degraded). See
   [provider-abstraction.md §8](provider-abstraction.md#8-health--failure-classification-decided-eng-0011a-planned).

---

## 5. Open questions (need a human decision)

> The transport model itself is **Decided** (ENG-0009: REST primary + constrained SSH, no node
> agent). The items below are **spec details that refine, but do not reopen, that decision** — most
> are flagged in ENG-0009 to **verify against a live PVE node**.

1. **TLS trust model for PVE nodes.** Fingerprint pinning per node, an operator-supplied CA, or
   require proper certs? Affects onboarding UX. (Distinct from the agent PKI — ENG-0005 — and from
   the operator dashboard TLS — ENG-0010; PVE node trust is its own still-open question.) This
   intersects the **SSH `known_hosts` / host-key pinning** strategy for the residue channel: the Core
   container needs a defined way to trust each node's SSH host key (pin on first onboard vs supplied
   fingerprint), tied to ENG-0010's image-needs-an-SSH-client note. **Owner decision; verify against
   a live node.**
2. **Where does the *optional* `virt-v2v`/`qemu-img` enhancement run** — on the PVE node over SSH
   (simplest, uses node CPU/disk) or a dedicated conversion container/host? The **baseline
   `qm importdisk` always runs on the PVE node** (ENG-0001), so this concerns only the optional
   conversion path. Shared with
   [ARCHITECTURE.md open questions](../ARCHITECTURE.md#open-questions) and
   [agent-protocol.md OQ 1](agent-protocol.md#9-open--resolved-questions).
3. **Cluster vs single-node addressing.** Do we register a cluster (one API entry point, resolve
   nodes dynamically) or individual nodes? `/cluster/resources` assumes a cluster; a lone node
   still answers it. Recommend registering a node and discovering the rest — confirm.
4. **SSH hardening shape (ENG-0009 sub-question).** Dedicated account + `sudo` for the few root ops
   (`qm importdisk`, guest-root mount/edit) **vs** a root-only forced-command key. Both lock the key
   to the bounded residue command set (§1); the choice trades least-privilege accounting against
   setup simplicity. **Owner decision; verify the residue command set runs cleanly under the chosen
   shape on a live node.**
5. **Exact API token ACL scopes per milestone (ENG-0009 sub-question).** A privilege-separated token
   silently 403s on any path not granted (gotcha 7). The **minimum ACL set per milestone** — read
   for 2.1 Observe, lifecycle/snapshot for 2.2, `VM.Allocate`/storage/`Sys.Modify` for 2.3
   provisioning + migration — must be enumerated so operators can scope tokens correctly. Also
   **verify against a live node whether any current PVE release exposes a REST disk-import endpoint**
   that would shrink the §3 SSH residue (would simplify, not change, the decision). **Verify against a
   live node.**

> **Deploy/Backup note (ENG-0006):** when those pillars land, `ProxmoxProvider` gains create-from-
> template / install-from-ISO and snapshot/export/restore surfaces (native PVE backup, PBS
> integration). The **backup buy-vs-build approach is deferred** (ENG-0006) — do not assume a PVE
> backup mechanism here yet.
