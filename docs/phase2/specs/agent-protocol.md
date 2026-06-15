# Agent Protocol — Core ↔ Windows Agent

> Component spec · expands [ARCHITECTURE.md §1 Topology](../ARCHITECTURE.md#topology) and the
> `VMentory.Agent` project row in [§Component breakdown](../ARCHITECTURE.md#1-vmentory-core).
> Anchored to **Decision 1**: Hyper-V is reached through an agent installed on the Windows host,
> **not** remote WinRM from Linux.

The Core runs in a Linux container; Hyper-V management is a Windows/PowerShell/WMI world. Rather
than bridge that gap with Linux→WinRM (TrustedHosts, Negotiate/Kerberos from non-domain Linux,
CredSSP for the second hop — all the pain Phase 1 already wrestles with locally:
[Reachability.cs:74](../../../Reachability.cs#L74) `EnsureTrustedHostAsync`,
[Reachability.cs:52](../../../Reachability.cs#L52) `EnsureWinRmServiceAsync`), we put a small
service **on the host** where PowerShell + the Hyper-V module already work natively. `HyperVProvider`
([provider-abstraction.md §4](provider-abstraction.md#4-how-the-two-providers-differ)) is then a
thin client of this protocol.

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

## 2. Transport: gRPC vs HTTP+JSON — recommendation

[ARCHITECTURE.md open questions](../ARCHITECTURE.md#open-questions-for-the-spec-agents) leaves this
open. Decision factors:

| Factor | gRPC | HTTP/1.1 + JSON |
|---|---|---|
| Streaming (scan progress, migration disk-copy %, live log) | First-class server-streaming | SSE/chunked — workable but ad hoc; Core already speaks SSE ([EventHub.cs](../../../EventHub.cs)) |
| Schema / contract | `.proto` is the contract, codegen both ends | Hand-kept DTOs (Phase-1 style: `record CredentialsDto` [Program.cs:543](../../../Program.cs#L543)) |
| Debuggability | Needs `grpcurl`/reflection; opaque on the wire | `curl`, browser, logs — trivial |
| mTLS | Native, idiomatic | Native in Kestrel/HttpClient |
| Large binary transfer (disk export) | Streaming works but gRPC is **not** the right pipe for multi-GB disks | Neither — see §6 |

**Recommendation: gRPC over HTTP/2 with TLS, for the control plane.** The agent's traffic is
inherently streaming (long inventory scans, per-step migration progress, tailing logs), and a
typed `.proto` contract between two .NET deployables removes a whole class of drift bugs that
hand-kept DTOs invite. The debuggability gap is real but bounded — provide a `--http-debug`
listener on the agent (plain JSON mirror of the unary RPCs) for field diagnosis. The disk-export
bulk path is **not** carried over the RPC channel either way (§6).

*If* the team weights "must `curl` it in the field" above streaming ergonomics, HTTP+JSON with
SSE for progress is a defensible second choice and reuses Core's existing SSE muscle — but accept
the manual contract maintenance.

---

## 3. Authentication & trust

Decision 1 plus [ARCHITECTURE.md §5 Security](../ARCHITECTURE.md#5-security-this-is-now-a-hosted-service-not-loopback)
require the agent to accept **only the Core's identity** — the agent runs as a privileged Windows
service on a Hyper-V host and must never be an open management endpoint.

**Recommendation: mutual TLS as the primary, with a bootstrap enrolment token.**

- **mTLS.** Core presents a client cert; agent pins the Core's cert/CA and rejects all else.
  Agent presents a server cert Core pins. This is the steady-state auth — no shared secret to
  leak, no replay.
- **Enrolment.** First contact uses a one-time, short-lived bootstrap token (operator pastes it
  into the agent installer or Core's "add host" flow) to exchange/sign the long-lived client
  cert. Mirrors how Phase-1 mints a per-session token ([Program.cs:528](../../../Program.cs#L528)
  `GenerateToken`, 24 random bytes, URL-safe base64) — reuse that generator for the bootstrap
  token.
- The agent **listens on the host's management interface only**, not `0.0.0.0` blindly; document
  a firewall rule scoped to the Core's address.

Short-lived signed tokens (the ARCHITECTURE alternative) are simpler to stand up but require a
rotation story and a shared signing secret in the secret store
([persistence-and-security.md §4](persistence-and-security.md#4-secret-handling)). mTLS pushes the
trust into PKI where it belongs for a long-running host service. **Recommend mTLS; fall back to
signed tokens only if PKI provisioning proves too heavy for the target operators.** See
*Open question 1*.

---

## 4. Endpoints / RPC surface

Mapped to `IVirtualizationProvider`
([provider-abstraction.md §2](provider-abstraction.md#2-the-interface)). gRPC method names shown;
the HTTP mirror is the obvious `POST /v1/<name>`.

| RPC | Maps to | Notes |
|---|---|---|
| `GetHealth` | agent heartbeat | Hyper-V module present? service running? agent version. Replaces Phase-1 reachability/Poller. |
| `GetHost` | `GetHostAsync` | Runs `QuickInfoScript`/`FullInventoryScript` host portion locally. |
| `GetVms` (server-stream) | `GetVmsAsync` | Streams VMs as enumerated rather than one big `ConvertTo-Json -Depth 10` blob ([Scanner.cs:325](../../../Scanner.cs#L325)). |
| `GetVmStats` (server-stream) | `GetStatsAsync` | Live perf counters; tick interval a request param. |
| `Lifecycle` | `LifecycleAsync` | start/stop/shutdown/reset/checkpoint. **New write surface** — Phase 1 is read-only. |
| `ExportDisk` (server-stream progress) | `ExportDiskAsync` | Returns a **staging locator + progress**, not disk bytes (§6). Locates VHDX via the path already in inventory ([Scanner.cs:281](../../../Scanner.cs#L281)). |
| `StreamLogs` (server-stream) | per-step migration log | Tail of a running operation; feeds [EventHub.cs](../../../EventHub.cs) → SSE. |

JSON shapes mirror Phase-1 inventory field names where parity matters
([Scanner.cs:251](../../../Scanner.cs#L251) onward) so the 2.0 dashboard maps 1:1.

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

## 6. Disk transfer is out-of-band

The multi-GB disk export for migration must **not** flow through the RPC control channel. The
agent exposes a converted/raw disk to the staging area (or streams it to a staging endpoint) over
a dedicated bulk path with resumable, checksummed transfer; the RPC channel carries only the
*locator* and *progress*. This keeps the control protocol responsive and lets the disk path use
the right tool (see virt-v2v/qemu-img orchestration in
[migration-job-model.md §4–5](migration-job-model.md#4-conversion-virt-v2v-wrapping)). Exact
staging mechanism is *Open question 2*.

---

## 7. Open questions (need a human decision)

1. **mTLS PKI ownership.** Who issues/rotates the agent and Core certs — Core acts as a tiny
   internal CA, or operators bring their own? Affects the installer UX and the enrolment flow
   (§3). **Owner decision.**
2. **Staging location for disk export.** Does the agent push the disk to a Core-side staging
   volume, to the PVE node directly, or to a neutral conversion host? This ties to the
   ARCHITECTURE open question on conversion-host placement
   ([ARCHITECTURE.md open questions](../ARCHITECTURE.md#open-questions-for-the-spec-agents)) and
   to [migration-job-model.md §4](migration-job-model.md#4-conversion-virt-v2v-wrapping).
3. **Agent self-update.** [ARCHITECTURE.md carry-over table](../ARCHITECTURE.md#what-carries-over-vs-what-gets-rebuilt)
   re-scopes Phase-1 [Updater.cs](../../../Updater.cs) and notes "the agent gets its own update
   path." MSI + scheduled check, or Core-pushed update over the authenticated channel? Out of
   scope for 2.0 but flag it now.
