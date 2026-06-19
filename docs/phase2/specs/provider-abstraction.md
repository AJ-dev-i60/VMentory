# Provider Abstraction

> Component spec · expands [ARCHITECTURE.md §2 Provider abstraction](../ARCHITECTURE.md#2-provider-abstraction).
> Anchored to Hyper-V via host agent (ENG-0001/0004) and orchestrate-proven-tools migration
> (ENG-0001/0006). **Scope note (ENG-0006):** the provider — and the capability model below — must
> now gate not only inventory/lifecycle/migration but also **Deploy** (create-from-template /
> install-from-ISO) and **Backup/Restore** (snapshot / export / restore) verbs as those pillars
> land. The interface and capability record grow; the design holds.

This is the pivot that turns a "Hyper-V tool" into a "platform". Everything above it
(`VMentory.Web`, the job engine) talks only to `IVirtualizationProvider`; everything below
it (`VMentory.Providers.HyperV`, `VMentory.Providers.Proxmox`) is transport-specific and
hidden. If this contract is right, adding a third platform is a new assembly, not a rewrite.

---

## 1. Design goals

1. **Phase-1 parity through the abstraction.** The Hyper-V dashboard must reproduce exactly
   what Phase 1 shows today ([Models.cs:34](../../../VMentory.Core/Models.cs#L34) `Vm`,
   [Models.cs:56](../../../VMentory.Core/Models.cs#L56) `Host`) — but reached through the provider, not
   through direct PowerShell. No regression is the 2.0 exit bar
   ([ROADMAP.md §2.0](../ROADMAP.md)).
2. **Capability honesty.** A provider advertises what it can actually do. The UI renders verbs
   from capabilities, so a "Migrate" or "Snapshot" button never appears for a target that will
   500 on it.
3. **Asymmetry is expected.** Hyper-V (via agent + PowerShell/WMI) and Proxmox (via REST + SSH)
   do not have the same primitives. The abstraction must not force a lowest-common-denominator;
   it must let each provider expose what it has and let the engine compose.

---

## 2. The interface

### 2.0 reality (built — slice 2, commit `2cb54fd`)

The interface that actually ships in
[`VMentory.Core/IVirtualizationProvider.cs`](../../../VMentory.Core/IVirtualizationProvider.cs) is
**intentionally lean** — only the read surface planted-Observe needs, plus the capability handles.
The verb methods sketched below are **deferred** (the capability *flags* already exist, so growing
this single-consumer interface later is non-breaking):

```csharp
public interface IVirtualizationProvider
{
    PlatformKind Platform { get; }
    ProviderCapabilities Capabilities { get; }
    Task<(bool Ok, string Error)> QuickConnectAsync(Host host, CancellationToken ct = default);
    Task<(bool Ok, string Error)> ScanAsync(Host host, CancellationToken ct = default);
}
```

`QuickConnectAsync`/`ScanAsync` take the Phase-1 `Host` and mutate the shared `Store` (the provider
resolves its own creds/port — credential-free contract). `HyperVProvider` wraps the existing static
`Scanner`/`ReachabilityChecker`. The richer ref/DTO surface below is the **target**, not yet built.

> **Return-shape change (decided, ENG-0011a — planned, not yet built).** The
> `(bool Ok, string Error)` tuple is **superseded** by a typed result that carries a per-stage
> **`HostFault` list** the provider is uniquely positioned to classify (e.g. PVE 401 vs 403). This is
> **Option A full-depth** — providers return the full fault list, not a single primary fault. See
> [§8 Health & failure classification](#8-health--failure-classification-decided-eng-0011a-planned).

### Target shape (illustrative — methods land in 2.2/2.3/2.5/2.6)

The shape in [ARCHITECTURE.md §2](../ARCHITECTURE.md#2-provider-abstraction) is the target contract.
Refining it for implementation:

```csharp
// TARGET — verb methods deferred; only Platform/Capabilities/QuickConnectAsync/ScanAsync exist in 2.0.
public interface IVirtualizationProvider
{
    PlatformKind Platform { get; }                       // HyperV | Proxmox
    ProviderCapabilities Capabilities { get; }

    // ---- inventory (Phase-1 parity, milestone 2.0 / 2.1) ----
    Task<HostInfo>               GetHostAsync(HostRef host, CancellationToken ct);
    Task<IReadOnlyList<VmInfo>>  GetVmsAsync(HostRef host, CancellationToken ct);
    Task<VmStats>                GetStatsAsync(VmRef vm, StatsWindow window, CancellationToken ct);

    // ---- management (milestone 2.2, capability-gated) ----
    Task                         LifecycleAsync(VmRef vm, LifecycleOp op, CancellationToken ct);

    // ---- migration primitives (milestone 2.3, capability-gated) ----
    Task<DiskExport>             ExportDiskAsync(VmRef vm, DiskRef disk, StagingTarget to, IProgress<TransferProgress> p, CancellationToken ct);
    Task                         ImportDiskAsync(VmRef shell, DiskImage img, IProgress<TransferProgress> p, CancellationToken ct);
    Task<VmRef>                  CreateVmShellAsync(HostRef host, VmSpec spec, CancellationToken ct);

    // ---- deploy (milestone 2.5) & backup/restore (milestone 2.6), capability-gated ----
    //  CreateFromTemplateAsync / AttachIsoAsync / CustomizeGuestAsync   (Deploy, ENG-0006)
    //  SnapshotAsync / ExportBackupAsync / RestoreBackupAsync           (Backup,  ENG-0006 — buy-vs-build deferred)
    //  These extend the same interface as the pillars land; the agent verb catalog grows in lockstep
    //  (see agent-protocol.md §4). Sketched, not committed, until those milestones.
}
```

Notes that matter for implementers:

- **Refs, not objects, are the addressing unit.** `HostRef`/`VmRef`/`DiskRef` are
  `(PlatformKind, providerHostId, nativeId)` tuples. For Hyper-V `nativeId` is the VM GUID
  (`$vm.Id`, already read in [Scanner.cs:288](../../../Scanner.cs#L288)); for Proxmox it is
  `{node}/{vmid}`. Never key on VM **name** across the boundary — Phase-1 `Store.RecordDiff`
  keys on `hostId:name` ([Store.cs:82](../../../Store.cs#L82)), which is fine for in-session
  diff but is **not** a stable migration identity. See *Open question 1*.
- **`IProgress<T>` is the bridge to SSE.** Long ops report through `IProgress`; `VMentory.Web`
  forwards onto the existing [EventHub.cs](../../../EventHub.cs) broadcast. Keeps the provider
  ignorant of transport. See [agent-protocol.md](agent-protocol.md#5-streaming-progress) and
  [migration-job-model.md](migration-job-model.md#6-per-step-logging--progress).
- **Every method takes a `CancellationToken`.** Phase-1 already threads CTs through every async
  path ([Reachability.cs:155](../../../Reachability.cs#L155)); migration steps need hard-stop
  ([migration-job-model.md §7](migration-job-model.md#7-stop-rollback-and-idempotency)).

---

## 3. Capability model

Capabilities are how the engine and UI ask "can this provider do X *here*" without a type
switch. **Built (slice 2, commit `2cb54fd`) as a `[Flags] ProviderCapability` enum** wrapped in a
`ProviderCapabilities` record with a `Supports(...)` helper — see
[`VMentory.Core/ProviderCapability.cs`](../../../VMentory.Core/ProviderCapability.cs) and
[ARCHITECTURE.md → Capability model](../ARCHITECTURE.md#capability-model--management-verbs-are-capability-gated-per-provider-eng-0007),
which mirror each other verbatim:

```csharp
[Flags]
public enum ProviderCapability
{
    None = 0,
    Inventory = 1 << 0, LiveStats = 1 << 1, HistoricalStats = 1 << 2,   // Observe
    Start = 1 << 3, Stop = 1 << 4, Reconfigure = 1 << 5, Snapshot = 1 << 6, Reset = 1 << 7,  // light mgmt
    ExportDisk = 1 << 8, ImportDisk = 1 << 9, CreateVmShell = 1 << 10,  // migration primitives
    Provision = 1 << 11, Backup = 1 << 12, Restore = 1 << 13,           // Deploy / Backup
}

public sealed record ProviderCapabilities(ProviderCapability Verbs)
{
    public bool Supports(ProviderCapability capability) => (Verbs & capability) == capability;
}
```

> **Note — flags, not a bool record.** An earlier draft of this spec proposed a record of explicit
> bool facts arguing a flags enum was "too coarse." The build chose the flags enum (ENG-0007's
> requirement is that HV be able to *advertise management verbs*, which flags satisfy cleanly), and
> ARCHITECTURE.md was written to match. The nuances the bool draft worried about are **modifiers, not
> verbs**, and stay out of the verb enum: e.g. `RequiresPowerOffForExport` (HV without checkpoint)
> and `Firmware` (BIOS/UEFI/Both) are **precheck inputs** the migration precheck step evaluates
> ([migration-job-model.md §3](migration-job-model.md#3-step-flow-hyper-v--proxmox-the-proven-path-eng-0001)),
> not capability flags. If a per-verb modifier ever needs to ride alongside the flags, add fields to
> the `ProviderCapabilities` record (its signature already reserves room) rather than reverting the
> enum.

**2.0 advertisement:** `HyperVProvider` advertises **`Inventory | LiveStats`** only
([HyperVProvider.cs](../../../HyperVProvider.cs)); the management/migration/deploy/backup flags are
reserved and flipped on as 2.2/2.3/2.5/2.6 wire their verb methods.

Capabilities are **static per provider type in 2.x** but are exposed as a **property** (not a
constant) so they may later depend on the connected version — e.g. PVE 8 vs 7, or a Hyper-V host
where the Hyper-V module is absent (Phase 1 already handles this case:
[Scanner.cs:272](../../../Scanner.cs#L272) checks `Get-Module -ListAvailable -Name Hyper-V`
and returns an empty VM list if missing). A host with no Hyper-V role should advertise
`Inventory` only.

**The UI rule:** a management, migration, deploy, or backup verb renders **only** if every provider
involved advertises the capability (via `Capabilities.Supports(...)`). Migration is the hard case —
it requires the *source* `ExportDisk` and the *target* `ImportDisk | CreateVmShell` plus a compatible
firmware match (the firmware/power-off modifiers are precheck inputs, not flags — see the note
above). The precheck step owns this evaluation
([migration-job-model.md §3](migration-job-model.md#3-step-flow-hyper-v--proxmox-the-proven-path-eng-0001)).
**Deploy** gates on `Provision`; **Backup/Restore** gate on `Backup` / `Restore` on the relevant
provider (ENG-0006). The agent advertises which verbs it serves via capability negotiation
([agent-protocol.md §8](agent-protocol.md#8-health-heartbeat--capability-negotiation-eng-0004)),
so an older agent version never gets offered a verb it can't run.

---

## 4. How the two providers differ

| Concern | `HyperVProvider` | `ProxmoxProvider` |
|---|---|---|
| Transport | Calls **VMentory.Agent** on the host (ENG-0001) over **gRPC/HTTP2 + mTLS** (ENG-0004); the agent runs the **natively-reimplemented** inventory/lifecycle logic that lives in [Scanner.cs](../../../Scanner.cs) / [Reachability.cs](../../../Reachability.cs) today — no winrun.py/Python | Direct **PVE REST** (scoped token auth) + **SSH** for disk ops; no agent ([proxmox-integration.md](proxmox-integration.md)) |
| Inventory source | `Get-VM`, `Get-VMHardDiskDrive`, `Get-VHD`, WMI `Win32_*`, KVP exchange ([Scanner.cs:243](../../../Scanner.cs#L243)) | `/cluster/resources`, `/nodes/{n}/qemu/{id}/status/current`, `/config` |
| Live stats | Agent reads perf counters → `VmStats` | `/nodes/{n}/qemu/{id}/rrddata` (native time series) |
| Historical stats | **Only from our own persisted snapshots** — Hyper-V keeps none | rrddata gives history for free; we still snapshot for cross-platform uniformity |
| VM identity | VM GUID | `{node}/{vmid}` |
| Firmware concept | `Generation` 1/2 ([Models.cs:39](../../../VMentory.Core/Models.cs#L39)); Gen2 = UEFI | `bios` = `seabios`/`ovmf`; `ovmf` = UEFI + EFI disk |
| Disk export | Locate VHDX path (already captured: [Scanner.cs:281](../../../Scanner.cs#L281)); copy/stream from staging; power-off or checkpoint required | `qm`/`qemu-img` over SSH; PVE storage abstraction (lvm/zfs/dir) |
| Disk format | VHDX | qcow2 / raw (storage-dependent) |

The asymmetry the abstraction must absorb cleanly: **Hyper-V has no native historical stats and
no API server** (hence the agent), while **Proxmox has both but no in-guest KVP-style OS
reporting** as rich as Hyper-V's `GuestIntrinsicExchangeItems`
([Scanner.cs:289](../../../Scanner.cs#L289)). Neither is the floor; the domain carries the
union and capabilities mark what's populated.

---

## 5. Generalizing the Phase-1 domain

Phase-1 types are already close to platform-neutral. The migration into `VMentory.Core.Domain`:

| Phase-1 type | Phase-2 fate |
|---|---|
| `Host` ([Models.cs:56](../../../VMentory.Core/Models.cs#L56)) | → `HostInfo`. Keep hardware/OS fields (they generalize). Add `PlatformKind Platform`, `ProviderCapabilities`. Drop UI/scan-state fields (`ScanState`, `AddError`, `Connecting`, `Reachability`) — those move to a separate registry/runtime-state record, not the inventory snapshot. |
| `Vm` ([Models.cs:34](../../../VMentory.Core/Models.cs#L34)) | → `VmInfo`. Keep `Name`, `State`, `VCpuCount`, `*RamMb`, `Vhds`, `NicCount`, `GuestOs`, `Uptime`. **`Generation` and `IntegrationServices` become Hyper-V-specific extension data**, not core fields (per [ARCHITECTURE.md §2](../ARCHITECTURE.md#2-provider-abstraction)). Add stable `nativeId`. |
| `Vhd` ([Models.cs:27](../../../VMentory.Core/Models.cs#L27)) | → `DiskInfo`. Keep `Path`, `ThickGb`/`ActualGb` (= provisioned/allocated, platform-neutral). Add `Format` (vhdx/qcow2/raw) and `BusKind`. |
| `Volume` ([Models.cs:20](../../../VMentory.Core/Models.cs#L20)) | → `StorageInfo`. Generalizes to PVE storage pools, not just Windows drive letters. |
| `Reachability` ([Models.cs:11](../../../VMentory.Core/Models.cs#L11)) | **Superseded by `HostHealth`/`HostFault` (ENG-0011a, §8).** The loose `Icmp`/`WinRm`/`Auth`/`ErrorDetail` booleans + strings stop being the UI source of truth; only the ICMP bit survives as the informational `HostHealth.IcmpReplied`. The three free-text channels (`AddError`/`ScanError`/`ErrorDetail`) collapse into one classified `Faults[]`. |
| `Credentials` ([Models.cs:121](../../../VMentory.Core/Models.cs#L121)) | The zero-on-dispose discipline **carries forward** but secrets no longer live in the domain object at all — they come from the secret store ([persistence-and-security.md](persistence-and-security.md#4-secret-handling)). |

**Extension data, not subclasses.** Resist a `HyperVVm : VmInfo` hierarchy — it leaks platform
types into `VMentory.Core` and the job engine. Carry provider-specific fields in a typed bag
(`IReadOnlyDictionary<string, string> ProviderExtra` or a discriminated `object? PlatformData`)
that only the owning provider and its UI fragment read.

---

## 6. Trade-offs and recommendation

- **One interface vs. segregated interfaces.** Splitting into `IInventoryProvider` /
  `IManagementProvider` / `IMigrationProvider` would let a provider implement only what it can.
  **Recommendation: keep the single interface, gate at runtime via `Capabilities`.** Rationale:
  the milestone order ([ROADMAP.md](../ROADMAP.md)) adds capability surfaces incrementally to
  *both* providers in lockstep, so interface segregation buys little and the engine would have
  to feature-probe by casting anyway. Throw `NotSupportedException` for an unadvertised verb;
  the UI guarantees it's never reached, the exception is defence-in-depth.
- **Sync capabilities vs. probed-at-connect.** Static-per-type is simpler and right for 2.0–2.2.
  Make it a property now so the version-dependent probe (PVE major version, Hyper-V role
  present) can slot in without a contract change.

---

## 7. Open questions (need a human decision)

1. **Stable VM identity for cross-session diff and migration.** Phase-1 diffs on
   `hostId:name` ([Store.cs:82](../../../Store.cs#L82)). For persisted historical diff
   ([persistence-and-security.md §2](persistence-and-security.md#2-what-is-persisted)) and for
   migration provenance, we need a stable key. Hyper-V VM GUID and PVE `{node}/{vmid}` are both
   stable enough — but a PVE VM that is itself migrated between nodes changes `node`. Do we key
   on `vmid` alone (cluster-unique) and treat `node` as mutable location? **Owner decision.**
2. **Where do non-inventory host fields live?** I propose splitting `Host` into immutable
   `HostInfo` (inventory snapshot) + mutable `HostRegistration` (address, creds reference,
   connection health). Confirm this split before the domain is generalized, since it shapes the
   persistence schema ([persistence-and-security.md §1](persistence-and-security.md#1-schema-sketch)).

---

## 8. Health & failure classification (decided, ENG-0011a; planned)

> **Decided (ENG-0011a, 2026-06-19) — planned, not yet built.** This is the UI-facing failure-surfacing
> slice of the broader (still-open) ENG-0011 observability contract: how a host's reachability/scan
> signals are **classified into a health tier and surfaced to the dashboard**, plus the provider-contract
> change that makes the classification authoritative server-side. It deliberately does **not** decide the
> rest of ENG-0011 (logging substrate, persisted sinks/retention, audit-vs-ops separation, the uniform
> per-transport diagnostic envelope). `VMentory.Core` is the **single source of truth** for this taxonomy
> — it is not restated anywhere else.

### The typed model (lives in `VMentory.Core`, alongside `ProviderCapability`/`PlatformKind`)

```csharp
enum HealthTier   { Unknown, Healthy, Unconfigured, Degraded, Unauthorized, Unreachable }
enum FailureStage { Dns, Icmp, TcpPort, Transport, Auth, Credentials, Scan }

// One classified fault at one stage. Code is the stable contract; Human is display text;
// Hint is a nullable high-confidence remediation; At is when it was observed.
record HostFault(string Code, HealthTier Tier, FailureStage Stage,
                 string Human, string? Hint, DateTimeOffset At);

class HostHealth {
    HealthTier Tier;            // worst-wins fold of Faults (Unknown while connecting)
    List<HostFault> Faults;     // full per-stage fault list — the drill-down, from day one
    bool IcmpReplied;           // informational only; NEVER folded into Tier
    DateTimeOffset CheckedAt;
}
```

This **replaces** the loose `Reachability` booleans + the three parallel free-text channels (§5,
`AddError`/`ScanError`/`Reachability.ErrorDetail`) as the source of truth for UI severity. `ErrorDetail`
is never actually rendered today, so nothing visible is lost.

### Six tiers + worst-wins fold

A host shows **one** tier — the highest-severity among its faults (`Unknown` is the transient
"checking…"/never-evaluated state, not a severity peak). Severity ordering (highest first):

`Unreachable > Unauthorized > Degraded > Unconfigured > Healthy` (`Unknown` while connecting).

| Tier | Meaning |
|---|---|
| `Healthy` | Mgmt plane reachable, auth OK, last scan succeeded (or reachable, not yet scanned). |
| `Degraded` | Reachable + authenticated but limited — **PVE 403** (valid token, missing privilege), a scan that failed *after* a good connect, or partial inventory. |
| `Unauthorized` | Credentials present but **rejected** — **PVE 401**, HV WinRM Access-Denied. Remediation = fix the credential. |
| `Unreachable` | Cannot reach the mgmt plane at all — DNS unresolved, mgmt TCP port closed, or transport error before auth. Remediation = fix DNS/network/firewall. |
| `Unconfigured` | Reachable but **no credentials set** — a *setup* state, not a failure. |
| `Unknown` | Mid-check / never polled / connecting. |

Two deliberate splits vs the Phase-1 `ok/warn/bad/unk` UI: **Unauthorized split from Degraded**
(remediation differs — fix creds vs grant a role) and **Unconfigured split out as a non-failure**
(reachable-but-credential-less is setup-in-progress, not red).

### Per-stage signal → code mapping (the CODE string is the contract)

The reachability/scan chain is evaluated stage by stage; each stage may emit a `HostFault` with a
**stable code** (UI/design and any future log query key off the code, never the human text). **ICMP is
the one stage that emits no fault and never affects the tier.**

| Stage (`FailureStage`) | Signal | Code | Tier |
|---|---|---|---|
| `Dns` | name does not resolve | `DNS_UNRESOLVED` | `Unreachable` |
| `Icmp` | ping reply / none | *(none — informational)* | **never** — surfaced as `icmpReplied` badge only |
| `TcpPort` | mgmt port closed/refused (HV 5985, PVE 8006) | `MGMT_PORT_CLOSED` | `Unreachable` (hint) |
| `Transport` | TLS/socket/timeout before an auth verdict | `MGMT_TRANSPORT_ERROR` | `Unreachable` |
| `Auth` | credentials rejected — **PVE 401**, HV WinRM Access-Denied | `AUTH_REJECTED` | `Unauthorized` |
| `Auth` | authenticated but **under-privileged** — **PVE 403** | `AUTH_INSUFFICIENT_PRIV` | `Degraded` (hint) |
| `Credentials` | reachable, **no credential configured** | `NO_CREDENTIALS` | `Unconfigured` (hint) |
| `Scan` | scan failed *after* a good connect | `SCAN_FAILED` | `Degraded` |

### Provider contract change (Option A — full depth) + the single evaluator

- **Provider methods return a typed health/fault result carrying a full per-stage fault list** (not the
  Phase-1 `(bool Ok, string Error)`). Providers emit the **platform-specific** faults the generic
  evaluator cannot know:
  - **`ProxmoxProvider`** must **split server-side** the PVE **401 → `AUTH_REJECTED` (Unauthorized)** vs
    **403 → `AUTH_INSUFFICIENT_PRIV` (Degraded)** — today both collapse into one "API token rejected"
    string at [ProxmoxProvider.cs:45,92](../../../ProxmoxProvider.cs#L45). This matches CLAUDE.md
    gotcha #13 (401 = wrong realm/token-id, 403 = valid-but-unprivileged); the knowledge exists, it just
    isn't surfaced. Plus `SCAN_FAILED` on a post-connect scan error.
  - **`HyperVProvider`** (via `Reachability`) distinguishes **Access-Denied → `AUTH_REJECTED`** from
    **transport timeout/connect-failure → `MGMT_TRANSPORT_ERROR`** by matching the well-known WinRM error
    substrings in the raw `AUTH_FAIL` message ([Reachability.cs:136-145](../../../Reachability.cs#L136)).
- **Generic stages stay in ONE evaluator, not the providers.** DNS, ICMP (informational), and the TCP
  mgmt-port probe are platform-independent. A single evaluator runs the generic stages, invokes the
  provider for the auth/scan stages, merges all faults, folds to a `Tier`, and produces one `HostHealth`.
- **Unify the two divergent classifiers.** Today the add-host path
  ([Program.cs:292](../../../Program.cs#L292): `"Host unreachable (ICMP failed)"` / `"WinRM port not
  responding"`) and the `Poller` re-check classify reachability **independently with divergent strings**.
  Both must call the **same** evaluator so a host shows the same tier whether just-added or just-polled.

### Wire contract (`/api/state` + existing SSE channel — no new channel)

Each host carries a `health` object over `/api/state` and every `EventHub` SSE broadcast (reusing the
existing typed-event channel, [EventHub.cs](../../../EventHub.cs)):

```json
"health": {
  "tier": "Degraded",
  "icmpReplied": false,
  "checkedAt": "2026-06-19T10:22:31Z",
  "faults": [
    { "code": "AUTH_INSUFFICIENT_PRIV", "stage": "Auth",
      "human": "Token authenticated but lacks the required privilege",
      "hint": "Grant the token the PVEAuditor role (or the verb-specific role) on / in Proxmox" }
  ]
}
```

- `tier` is the **server-composed** worst-wins tier — the dashboard renders it **verbatim** and must not
  re-derive it (the three Phase-1 JS re-derivers `hostHealth`/`healthTip`/`subLine` are replaced by a
  tier→badge map + `faults[]` render).
- `icmpReplied` drives a greyed informational badge only.
- `faults[]` is the full per-stage drill-down; each `{ code, stage, human, hint }` with `hint` nullable.
- `hint` is populated **only high-confidence** (`AUTH_REJECTED` PVE 401, `AUTH_INSUFFICIENT_PRIV` PVE
  403, `NO_CREDENTIALS`, `MGMT_PORT_CLOSED`), **null** elsewhere — we do not guess remediations.

This `health` JSON is the contract that **unblocks the dashboard UI rebuild**.

### Back-compat / transition

The old `Reachability` booleans + `AddError`/`ScanError` strings stay on the wire **only until** the
dashboard renders `health.*`, then are removed in the same UI-rebuild slice. During the transition the
evaluator is the **single writer** of both the new `HostHealth` and the derived legacy fields, so
nothing reads a stale parallel value. Exact cross-path removal sequencing is a flagged build-time
sub-question in ENG-0011a (not a design decision).

### Open build-time sub-question (does not reopen ENG-0011a)

- **Slice-5 Proxmox SSH transport faults.** When the constrained SSH transport (ENG-0009, slice 5)
  lands, an SSH connect/auth failure is a *second* transport stage distinct from the REST mgmt auth.
  Whether SSH gets its own `FailureStage`/codes (e.g. `SSH_TRANSPORT_ERROR`, `SSH_AUTH_REJECTED`) — and
  how it interacts with the single-root-vs-per-node SSH-user choice (open in ENG-0012, §4a) — is deferred
  to the slice-5 build. The `FailureStage` enum is designed to grow.
