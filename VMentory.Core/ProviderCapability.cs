namespace VMentory.Core;

// What a provider can actually do. Capabilities gate both the UI (only render verbs the target
// supports) and the operations engine (refuse to dispatch an unadvertised verb) — avoids
// "Migrate"/"Deploy" buttons that 500. Per ENG-0007 the model must allow *management* verbs on the
// Hyper-V provider (light management, not source-only) so 2.2/2.3 land without reworking this.
// See docs/phase2/ARCHITECTURE.md → "Capability model".
[Flags]
public enum ProviderCapability
{
    None             = 0,

    // Observe — every provider has these (the planted foundation).
    Inventory        = 1 << 0,   // hosts, VMs, disks, volumes
    LiveStats        = 1 << 1,   // current CPU/mem/IO
    HistoricalStats  = 1 << 2,   // time-series / rrddata

    // Light management — HV advertises at least Start|Stop|Reconfigure; PVE has all of these.
    Start            = 1 << 3,
    Stop             = 1 << 4,   // includes graceful shutdown + hard stop variants
    Reconfigure      = 1 << 5,   // vCPU/memory (+ optionally disks/NICs — scope pinned in 2.2)
    Snapshot         = 1 << 6,   // checkpoint / snapshot create-delete-revert
    Reset            = 1 << 7,

    // Migration primitives — capability-gated; not all providers do all of these.
    ExportDisk       = 1 << 8,   // HV provider: locate/expose flat VHDX
    ImportDisk       = 1 << 9,   // PVE provider: qm importdisk
    CreateVmShell    = 1 << 10,  // provision a target shell

    // Deploy / Backup — PVE-first; HV deliberately does NOT advertise these (ENG-0007).
    Provision        = 1 << 11,  // create-from-template / install-from-ISO
    Backup           = 1 << 12,
    Restore          = 1 << 13,
}

// Per-provider advertisement of supported verbs, checked before any verb is offered or dispatched.
public sealed record ProviderCapabilities(ProviderCapability Verbs)
{
    public bool Supports(ProviderCapability capability) => (Verbs & capability) == capability;
}
