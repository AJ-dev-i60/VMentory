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
   what Phase 1 shows today ([Models.cs:34](../../../Models.cs#L34) `Vm`,
   [Models.cs:56](../../../Models.cs#L56) `Host`) — but reached through the provider, not
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

The shape in [ARCHITECTURE.md §2](../ARCHITECTURE.md#2-provider-abstraction) is the contract.
Refining it for implementation:

```csharp
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
switch. A flags enum is too coarse (a provider may support snapshots but not live migration,
may support disk export but only while powered off). Use a record of explicit facts:

```csharp
public sealed record ProviderCapabilities
{
    public bool Inventory          { get; init; }   // always true
    public bool LiveStats          { get; init; }   // HV: agent perf counters; PVE: rrddata
    public bool HistoricalStats    { get; init; }   // PVE: rrddata native; HV: only from our snapshots
    public LifecycleOp SupportedLifecycle { get; init; } // [Flags]
    public bool SnapshotCheckpoint { get; init; }
    public bool DiskExport         { get; init; }
    public bool DiskImport         { get; init; }
    public bool CreateVmShell      { get; init; }
    public bool RequiresPowerOffForExport { get; init; } // HV without checkpoint: true
    public FirmwareSupport Firmware { get; init; }   // BIOS | UEFI | Both

    // ---- Deploy (2.5) & Backup (2.6) — ENG-0006; added as those pillars land ----
    public bool CreateFromTemplate { get; init; }   // golden-image / template provisioning
    public bool InstallFromIso     { get; init; }   // ISO-library install
    public bool GuestCustomization { get; init; }   // unattend/cloud-init/network (shared with Migrate)
    public bool Backup             { get; init; }   // snapshot/export
    public bool Restore            { get; init; }   // restore (to this platform)
}
```

Capabilities are **static per provider type in 2.x** but should be a property (not a constant)
because they may later depend on the connected version — e.g. PVE 8 vs 7, or a Hyper-V host
where the Hyper-V module is absent (Phase 1 already handles this case:
[Scanner.cs:272](../../../Scanner.cs#L272) checks `Get-Module -ListAvailable -Name Hyper-V`
and returns an empty VM list if missing). A host with no Hyper-V role should advertise
`Inventory` only.

**The UI rule:** a management, migration, deploy, or backup verb renders **only** if every provider
involved advertises the capability. Migration is the hard case — it requires the *source*
`DiskExport` and the *target* `DiskImport` + `CreateVmShell` + matching `Firmware`. The precheck step
owns this evaluation ([migration-job-model.md §3](migration-job-model.md#3-step-flow-hyper-v--proxmox-the-proven-path-eng-0001)).
**Deploy** gates on `CreateFromTemplate` / `InstallFromIso` (+ `GuestCustomization`); **Backup/Restore**
gate on `Backup` / `Restore` on the relevant provider (ENG-0006). The agent advertises which verbs it
serves via capability negotiation ([agent-protocol.md §8](agent-protocol.md#8-health-heartbeat--capability-negotiation-eng-0004)),
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
| Firmware concept | `Generation` 1/2 ([Models.cs:39](../../../Models.cs#L39)); Gen2 = UEFI | `bios` = `seabios`/`ovmf`; `ovmf` = UEFI + EFI disk |
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
| `Host` ([Models.cs:56](../../../Models.cs#L56)) | → `HostInfo`. Keep hardware/OS fields (they generalize). Add `PlatformKind Platform`, `ProviderCapabilities`. Drop UI/scan-state fields (`ScanState`, `AddError`, `Connecting`, `Reachability`) — those move to a separate registry/runtime-state record, not the inventory snapshot. |
| `Vm` ([Models.cs:34](../../../Models.cs#L34)) | → `VmInfo`. Keep `Name`, `State`, `VCpuCount`, `*RamMb`, `Vhds`, `NicCount`, `GuestOs`, `Uptime`. **`Generation` and `IntegrationServices` become Hyper-V-specific extension data**, not core fields (per [ARCHITECTURE.md §2](../ARCHITECTURE.md#2-provider-abstraction)). Add stable `nativeId`. |
| `Vhd` ([Models.cs:27](../../../Models.cs#L27)) | → `DiskInfo`. Keep `Path`, `ThickGb`/`ActualGb` (= provisioned/allocated, platform-neutral). Add `Format` (vhdx/qcow2/raw) and `BusKind`. |
| `Volume` ([Models.cs:20](../../../Models.cs#L20)) | → `StorageInfo`. Generalizes to PVE storage pools, not just Windows drive letters. |
| `Reachability` ([Models.cs:11](../../../Models.cs#L11)) | Hyper-V-specific concept (ICMP/WinRm/Auth). Becomes agent connection health, not a core domain type. |
| `Credentials` ([Models.cs:121](../../../Models.cs#L121)) | The zero-on-dispose discipline **carries forward** but secrets no longer live in the domain object at all — they come from the secret store ([persistence-and-security.md](persistence-and-security.md#4-secret-handling)). |

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
