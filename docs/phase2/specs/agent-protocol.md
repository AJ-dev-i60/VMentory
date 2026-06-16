# Agent Protocol — Core ↔ Hyper-V Host Agent

> Component spec · expands [ARCHITECTURE.md §1 Topology](../ARCHITECTURE.md#topology) and the
> `VMentory.Agent` project row in [§Component breakdown](../ARCHITECTURE.md#1-vmentory-core).
> **Decided:** the agent is reimplemented **natively in .NET** (no winrun.py / Python, ENG-0001),
> ships as a **NativeAOT single binary** and is a **constrained verb executor** (ENG-0004);
> transport is **gRPC over HTTP/2 + mTLS** (ENG-0004); install is **manual + enrollment token**
> (ENG-0003); PKI is a **private CA in Core** (ENG-0005). This spec is the propagation target for
> ENG-0001/0003/0004/0005.
>
> **⚠ Scope & sequencing (ENG-0009 re-baseline, 2026-06-16 — read first):** this agent is the
> **Hyper-V migration-source transport only.** It is **NOT** the Proxmox transport — Proxmox is
> driven by its native REST API + a constrained SSH key with **no node agent** (ENG-0009,
> [proxmox-integration.md](proxmox-integration.md)). The earlier "one agent serves both platforms /
> all Linux host roles" framing is **withdrawn.** This agent + its private-CA mTLS PKI are also **NOT
> front-loaded 2.0 foundation work**: they are **demoted off the 2.0 critical path and sequenced with
> the HV→PVE migration slice (2.3-era)**, their only release-1 consumer. The first containerized
> releases (Core + login/RBAC + planted Observe + Proxmox management/Deploy) **do not touch this
> agent.** The runtime/security *design* below is unchanged and still correct — only its scope and
> sequencing are corrected (ENG-0001/0003/0004/0005 amendments, all Decided via ENG-0009).

The Core runs in a Linux container; Hyper-V management is a Windows/PowerShell/WMI world. Rather
than bridge that gap with Linux→WinRM (TrustedHosts, Negotiate/Kerberos from non-domain Linux,
CredSSP for the second hop — all the pain Phase 1 already wrestles with locally:
[Reachability.cs:74](../../../Reachability.cs#L74) `EnsureTrustedHostAsync`,
[Reachability.cs:52](../../../Reachability.cs#L52) `EnsureWinRmServiceAsync`), we put a small
service **on the host** where the Hyper-V module already works natively. `HyperVProvider`
([provider-abstraction.md §4](provider-abstraction.md#4-how-the-two-providers-differ)) is then a
thin client of this protocol.

The agent targets **Windows (Hyper-V)** for release 1. The earlier "same codebase also serves future
Linux host roles" framing is **deferred** (ENG-0009 amendment of ENG-0004): the Linux host role this
runtime once anticipated is **not** a Proxmox-node role — Proxmox stays API+SSH with no agent — so it
is shelved against a hypothetical future non-Proxmox Linux virtualization target, not built now. The
NativeAOT/cross-compile design is retained should that target ever arrive. The agent remains the
**gate for migration** (ENG-0001): there is **no winrun.py fallback**, so the agent's migration verbs
must exist before 2.3 starts — but note this gate now sequences *with* the migration slice, not as a
front-loaded 2.0 build (see the scope banner above).

> **The `migrate-vm` skill is the behavioral reference (ENG-0001), not shipping code.** Its
> `winrun.py`/WinRM path and the `references/centralized-access.md` 401 gotcha list (NetBIOS-vs-FQDN,
> local-admin requirement, GPO linkage, elevated gpupdate) document *what the agent must do and the
> access model it replaces* — the agent reimplements that behavior in-process under its own service
> identity, eliminating the stored Windows domain password.

---

## 1. What moves into the agent

The agent **is** Phase-1 `Scanner` + `Reachability`, minus the remote-invocation wrapper:

- [Scanner.cs `FullInventoryScript`](../../../Scanner.cs#L243) and
  [`QuickInfoScript`](../../../Scanner.cs#L226) run **locally** on the host. The whole
  `Invoke-Command -ComputerName … -Credential … -Authentication Negotiate` wrapper
  ([Scanner.cs:203](../../../Scanner.cs#L203) `BuildRemoteWrapper`) **disappears** — there is no
  remote hop anymore. The inner script block runs in-process.
- That deletes a pile of Phase-1 gotchas at a stroke: TrustedHosts management
  ([Reachability.cs:74](../../../Reachability.cs#L74)), the local-WinRM-service requirement
  ([Reachability.cs:52](../../../Reachability.cs#L52)), Negotiate auth from off-box, and the
  PowerShell-5.1 `-File`-vs-`-Command-` stdout-swallowing bug
  ([Reachability.cs:153](../../../Reachability.cs#L153), CLAUDE.md gotcha 7).
- The agent keeps the local PowerShell-execution discipline that survives: read stdout/stderr
  concurrently to avoid pipe-buffer deadlock ([Reachability.cs:179](../../../Reachability.cs#L179),
  CLAUDE.md gotcha 9), and `string.Format` (not C# interpolation) for any PS that contains
  `{}` blocks (CLAUDE.md gotcha 1). Even better: prefer the **Hyper-V .NET/WMI APIs in-process**
  over shelling to `powershell.exe` where practical, since the agent is itself a .NET service —
  but the WMI KVP guest-OS query ([Scanner.cs:289](../../../Scanner.cs#L289)) is fiddly enough
  that reusing the proven PS is acceptable for 2.0 parity.

**Reachability inverts.** Phase 1 has Core probe ICMP/TCP/auth *toward* the host. With an agent,
the agent is either reachable or not; Core probes **the agent endpoint**, and the agent reports
*host-local* health (Hyper-V service up, module present). The Phase-1
[Poller.cs](../../../Poller.cs) 30 s re-check becomes an agent heartbeat/health poll.

---

## 2. Transport — gRPC over HTTP/2 + mTLS (decided, ENG-0004)

**Decided (ENG-0004): gRPC over HTTP/2 with mutual TLS for the control plane.** This is no longer
an open question. The rationale recorded in ENG-0004:

- **Strongly-typed, versioned `.proto` contract** between two .NET deployables removes a class of
  drift bugs that hand-kept DTOs invite, and pairs naturally with **capability negotiation** (§8)
  and AOT **source-gen** (no runtime reflection — a NativeAOT constraint).
- **Streaming-friendly** for the agent's inherently streaming traffic — long inventory scans,
  per-step migration/deploy/backup progress, live log tailing.
- **mTLS both directions** (§3): the agent presents its enrolled client identity; the agent pins
  Core's root. The listener is firewalled to Core's address.

| Factor | How it's handled |
|---|---|
| Streaming (scan progress, migration step %, live log) | gRPC server-streaming → Core `IProgress<T>` → [EventHub.cs](../../../EventHub.cs) SSE (§5) |
| Schema / contract | `.proto` is the contract, **source-generated** both ends (AOT-safe) |
| Debuggability | `grpcurl`; an optional plain-JSON `--http-debug` unary mirror for field diagnosis |
| Large binary transfer (disk) | **Not** over the RPC channel — out-of-band, a Proxmox-node concern (§6) |

The disk-export bulk path is **not** carried over the RPC channel (§6) — gRPC is the *control*
plane only.

---

## 3. Authentication, trust & enrollment (decided, ENG-0003/0005)

The agent accepts **only the Core's identity** — it runs as a privileged service on a host and must
never be an open management endpoint. **mTLS is decided** (ENG-0004); the PKI that backs it is
decided (ENG-0005); install + enrollment is decided (ENG-0003). No fallback to shared signed
tokens — the earlier "fall back to signed tokens" option is closed.

### Steady-state — mutual mTLS (ENG-0004/0005)
- The agent presents its **enrolled client cert**; Core presents a server cert; the agent pins
  **Core's root** (not the leaf — see structure below). No shared secret to leak, no replay.
- Certs are **short-lived** (days/weeks) and **auto-renewed over the existing mTLS channel** before
  expiry — the same trusted channel used for self-update (§7). No CRL/OCSP.
- **Revocation = stop renewing + a registry allow/deny list in Core** (ENG-0005). An identity
  removed from the allow-list (or added to deny) stops being honored at the next handshake.
- The agent **listens on the host's management interface only**, firewalled to Core's address.

### PKI structure (ENG-0005)
- A **private CA inside Core** is the v1 default (external-CA / AD CS seam deferred). An offline-ish
  **root** signs a single **intermediate**; the **intermediate** does day-to-day signing.
- Agents pin the **root**; Core signs agent + server certs with the **intermediate** — so the
  signing key can rotate **without a fleet-wide re-pin**.
- The CA private key lives in [`ISecretStore`](persistence-and-security.md#4-secret-handling)
  (ENG-0002), KEK-wrapped — the natural case for the opt-in operator-passphrase KEK mode.

### Enrollment handshake (ENG-0003/0005)
Install is **manual / org-managed** (MSI / GPO / SCCM / Intune) — **no remote push-install**, and
VMentory never receives an admin credential (ENG-0003). Install *includes* enrollment:

1. Operator generates a **short-lived, single-use enrollment token** in Core (stored **hashed**,
   short TTL); pastes it into the installer / first-run. Reuse the Phase-1 generator
   ([Program.cs:528](../../../Program.cs#L528) `GenerateToken`, 24 random bytes, URL-safe base64).
2. Agent connects and validates Core's server cert against an operator-shown **fingerprint/pin**
   (trust-on-first-use, gated by the token).
3. Agent **generates its keypair locally** (the **private key never leaves the host**), sends a
   **CSR + token**; Core verifies the token (single-use, short TTL), signs the client cert with the
   **intermediate**, and returns the cert **+ the root to pin**.
4. Steady state: mutual mTLS; auto-renew before expiry over the same channel.

> **Update is separate from install** (ENG-0003) and does **not** reuse the manual path — once
> enrolled, the agent self-updates over its mTLS channel (§7).

---

## 4. The constrained verb catalog (ENG-0004)

The agent exposes a **fixed, versioned verb set** — **never arbitrary PowerShell / RCE** (ENG-0004).
This is the decisive security upgrade over `winrun.py`, which ran arbitrary remote PowerShell. The
catalog is **versioned** and grows with the pillars (ENG-0006); Core and agent negotiate the
supported version + capabilities at connect (§8). gRPC method names shown.

> **On the milestone labels below (ENG-0009 sequencing):** the parenthesized milestones indicate the
> *capability tier*, not that the agent is built early. The whole agent — **including** the inventory
> tier — is **HV-only** and lands **with the migration slice (2.3-era)**, not as front-loaded 2.0
> foundation. Hyper-V hosts are inventoried through the agent **once it exists**; until then HV
> inventory in the first releases is reached via the existing `HyperVProvider` path (planted Observe).
> The verb tiers still ship in this order *within* the agent's own delivery.

**Foundation / inventory (2.0) — maps to `IVirtualizationProvider`
([provider-abstraction.md §2](provider-abstraction.md#2-the-interface)):**

| RPC | Maps to | Notes |
|---|---|---|
| `GetHealth` (heartbeat) | agent health | Hyper-V module present? service running? agent + protocol version. Replaces Phase-1 reachability/Poller ([Poller.cs](../../../Poller.cs)). |
| `GetHost` | `GetHostAsync` | Runs the host-inventory logic **in-process** (the reimplemented `QuickInfoScript`/`FullInventoryScript`, [Scanner.cs:226](../../../Scanner.cs#L226)). |
| `GetVms` (server-stream) | `GetVmsAsync` | Streams VMs as enumerated rather than one big `ConvertTo-Json -Depth 10` blob ([Scanner.cs:325](../../../Scanner.cs#L325)). |
| `GetVmStats` (server-stream) | `GetStatsAsync` | Live perf counters; tick interval a request param. |

**Lifecycle + migration control (2.2 — the migration gate, ENG-0001/0004):**

| RPC | Maps to | Notes |
|---|---|---|
| `Lifecycle` | `LifecycleAsync` | start/stop/shutdown/reset/checkpoint. **New write surface.** |
| `ResolveCheckpoints` | migration precheck/quiesce | Merge `.avhdx` → flat `.vhdx` (the skill's checkpoint-merge step). |
| `Quiesce` | migration step 2 | Graceful shutdown vs checkpoint, **by operator choice** (ENG-0006) — the verb takes the mode; it does not decide policy. |
| `LocateDisks` / `ExposeDisk` (stream progress) | `ExportDiskAsync` | Returns a **staging locator + progress**, not disk bytes (§6). Locates VHDX via the inventory path ([Scanner.cs:281](../../../Scanner.cs#L281)). Disk *transfer* is a Proxmox-node concern (ENG-0001), not an agent push. |
| `StreamLogs` (server-stream) | per-step operation log | Tail of a running operation; feeds [EventHub.cs](../../../EventHub.cs) → SSE. |

**Deploy + Backup verbs (later pillars, ENG-0006) — catalog grows, design unchanged:**

| RPC (sketch) | Pillar | Notes |
|---|---|---|
| `CreateVm` / `AttachIso` / `AttachTemplateDisk` | Deploy (2.5) | VM creation from ISO/golden image on the host side. |
| `CustomizeGuest` | Deploy + Migrate | Unattend/sysprep/network injection — the shared guest-customization layer (ENG-0006). *Engine choice is an open ENG topic.* |
| `Snapshot` / `ExportBackup` / `RestoreBackup` | Backup (2.6) | Snapshot/export/restore. *Backup buy-vs-build is deferred (ENG-0006); these verbs are sketched, not committed.* |

Every executed verb is **idempotent/resumable** (ENG-0004 — survives an agent restart mid-job,
ties to [migration-job-model.md §7](migration-job-model.md#7-stop-rollback-and-idempotency)) and
**audit-logged back to Core's history store** (ENG-0004,
[persistence-and-security.md §6](persistence-and-security.md#6-audit-log)). JSON/proto shapes mirror
Phase-1 inventory field names where parity matters ([Scanner.cs:251](../../../Scanner.cs#L251)
onward) so the 2.0 dashboard maps 1:1.

---

## 5. Streaming progress

Long operations report incrementally; the agent server-streams progress frames, `HyperVProvider`
surfaces them as `IProgress<T>`
([provider-abstraction.md §2](provider-abstraction.md#2-the-interface)), and `VMentory.Web`
forwards onto the existing SSE broadcast ([EventHub.cs:51](../../../EventHub.cs#L51) `Broadcast`).
This is the same data path Phase 1 uses for `scanProgress`/`scanComplete`
([Program.cs:339](../../../Program.cs#L339), [Program.cs:405](../../../Program.cs#L405)) — the
agent just becomes the upstream source instead of an in-process scan task.

---

## 6. Disk transfer is out-of-band — a Proxmox-node concern (ENG-0001)

The multi-GB disk for migration must **not** flow through the RPC control channel, and — decided in
ENG-0001 — it is **not** an agent push either. **Disk transfer stays a Proxmox-node concern:** the
PVE node mounts the Hyper-V disk share over **CIFS** and `qm importdisk` reads the VHDX straight off
the mount (the proven `migrate-vm` path). The agent's role is to **locate and expose** the flat
VHDX (after checkpoint merge) and report *locator + progress* over the RPC channel; it does not move
the bytes. The RPC channel carries only control + progress. See
[migration-job-model.md §3–4](migration-job-model.md#3-step-flow-hyper-v--proxmox-the-proven-path-eng-0001) and
[proxmox-integration.md §3](proxmox-integration.md#3-rest-vs-ssh--the-boundary).

The SMB/CIFS credential the node uses comes from [`ISecretStore`](persistence-and-security.md#4-secret-handling)
injected at job runtime (ENG-0002) — replacing the skill's plaintext `/root/.smbcreds` file.

---

## 7. Self-update — over the agent's own mTLS channel (decided, ENG-0004)

Install is manual (ENG-0003); **update is not, and does not reuse the install path.** Once enrolled
and trusted (§3), the agent self-updates over its mTLS channel:

- Core hosts **signed agent packages**; on check-in the agent compares version, downloads,
  **verifies the signature**, stages, **atomically swaps**, and restarts.
- A **watchdog / bootstrapper** survives the main-agent restart and **rolls back on a failed
  post-update health check** (`GetHealth`, §4).
- Reuse the Phase-1 [Updater.cs](../../../Updater.cs) apply-on-launch + background-download pattern;
  the transport is the mTLS channel, not GitHub Releases.
- Binaries are **signed** (Authenticode on Windows, ENG-0004).

> *Open sub-question (ENG-0004):* code-signing certificate ownership/custody for agent packages —
> who signs, where the key lives (intersects ENG-0002). Distinct from the transport CA (ENG-0005).

---

## 8. Health, heartbeat & capability negotiation (ENG-0004)

- **Heartbeat / health** (`GetHealth`, §4): Core's poller consumes it; the agent survives host
  reboot via the Windows Service (SCM auto-restart) / systemd unit. Replaces the Phase-1
  [Poller.cs](../../../Poller.cs) 30 s reachability re-check — reachability **inverts**: Core probes
  *the agent endpoint*, and the agent reports *host-local* health (Hyper-V service up, module
  present), rather than Core probing ICMP/TCP/auth toward the host.
- **Versioned protocol + capability negotiation** (ENG-0004): Core and agent may differ by a version
  during a rollout. At connect they negotiate the supported protocol version and the agent's verb
  **capabilities** — feeding the provider [capability model](provider-abstraction.md#3-capability-model)
  so the UI never offers a verb the connected agent version can't serve.

---

## 9. Open / resolved questions

**Resolved (do not re-open):**
- ~~Transport gRPC vs HTTP+JSON~~ → **gRPC/HTTP2 + mTLS** (ENG-0004).
- ~~mTLS PKI ownership~~ → **private CA in Core**, root+intermediate, short-lived + auto-renew (ENG-0005).
- ~~Auth fallback to signed tokens~~ → **closed; mTLS only** (ENG-0004).
- ~~Agent self-update mechanism~~ → **mTLS channel + watchdog rollback** (ENG-0004).
- ~~Disk-export staging push from the agent~~ → **closed; disk transfer is a Proxmox-node concern** (ENG-0001).

**Still open (need a human decision; specs proceed against the current recommendation):**
1. **Conversion-host placement.** Where the *optional* `virt-v2v`/`qemu-img` enhancement runs — PVE
   node over SSH vs a dedicated conversion host. (The *baseline* `qm importdisk` runs on the PVE
   node; this only concerns the optional conversion step.) Shared with
   [proxmox-integration.md OQ](proxmox-integration.md#5-open-questions-need-a-human-decision) and
   [migration-job-model.md OQ](migration-job-model.md#8-open-questions-need-a-human-decision).
2. **gRPC `.proto` verb surface for v1** (ENG-0004 sub-question) — the exact contract for management
   vs migration step-graph verbs; a spec detail to lock before 2.0 codegen.
3. **Cert TTL + renewal-window defaults** (ENG-0005 sub-question) — e.g. 14-day cert, renew at 7 days.
4. **Enrollment-token host-binding** (ENG-0005 sub-question) — bind the single-use token to an
   expected host identity to harden TOFU.
5. **Linux agent host roles** — **largely closed by ENG-0009:** Proxmox stays API+SSH with **no
   agent**, so there is no Linux *node* agent in scope. What remains open is only a *hypothetical
   future* non-Proxmox Linux virtualization target (would revive the cross-platform agent) and the
   separate question of an **in-guest** customization agent (sysprep/cloud-init that must run *inside*
   a guest) — a distinct future ENG topic that does **not** revive a Proxmox-node agent. Flag to
   verify when/if those targets appear; out of scope for release 1.
