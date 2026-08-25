using System.Text.Json.Serialization;

namespace VMentory.Core.Estate;

// ── Estate view (ENG-0015) ────────────────────────────────────────────────────
// The estate is the set of *physical machines* the operator is responsible for, whether or not
// each one is a hypervisor VMentory can inventory. A Machine is the join point between three
// independent sources: the hardware monitor (iDRAC via OpenManage, keyed by service tag), the
// hypervisor inventory (a registered Host, matched by address), and the remediation tracker
// (ActionItems that name the machine). None of the three knows about the others.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HardwareStatus { Ok, Warning, Critical, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FaultSeverity { Info, Warning, Critical }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MachineKind { HyperV, Proxmox, BareMetal, Storage, Appliance }

// One concrete thing wrong with a machine's hardware, as read from the out-of-band monitor.
// Key is stable across polls (device tag + component) so the collector can recognise a fault it
// has already raised an action for — the dedupe contract between HardwareFault and ActionItem.
public record HardwareFault(
    string Key,
    FaultSeverity Severity,
    string Component,     // "Disk bay 5", "PSU 1", "Battery", "Storage"
    string Detail);       // human sentence, e.g. "ST960020 CLAR600 558 GB — predictive failure"

public class HardwareDevice
{
    public string ServiceTag { get; set; } = "";
    public string DeviceName { get; set; } = "";     // as the monitor labels it (often stale)
    public string Model { get; set; } = "";
    public string ManagementIp { get; set; } = "";
    public long MonitorId { get; set; }
    public HardwareStatus Status { get; set; } = HardwareStatus.Unknown;
    public bool Connected { get; set; }
    public Dictionary<string, HardwareStatus> Subsystems { get; set; } = new();
    public List<HardwareFault> Faults { get; set; } = [];
}

// One reading of the whole hardware estate at an instant — the same append-only snapshot pattern
// as InventorySnapshotEntity, so "what did the monitor say at the time" is always answerable.
public class HardwareSnapshot
{
    public DateTimeOffset TakenAt { get; set; }
    public string Source { get; set; } = "";         // "ome"
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<HardwareDevice> Devices { get; set; } = [];
}

// A guest recorded by hand for a machine VMentory cannot inventory live (today: every Hyper-V host,
// per ENG-0013). Carries where it came from and when it was checked, because a static list is a
// claim about the past and the UI must say so.
public record StaticVm(string Name, string? Address, string State, string? Note);

// ── Remediation tracker ───────────────────────────────────────────────────────

// Ordered by consequence, not effort. The UI works top-down.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionPriority { Critical = 1, High = 2, Medium = 3, Low = 4 }

// The remediation class — the logistics dimension. OnSite items batch into one visit; Purchase
// items list what to bring so the visit is not wasted.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionClass { Remote, OnSite, Purchase }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionStatus { Open, InProgress, Blocked, Done, Dismissed }

// The VMs that go dark if an action is carried out on its machines — computed, never stored.
public record ImpactVm(string Name, string? Address, string State, string Source);   // Source: live | static
public record ImpactMachine(string Key, string Name, string Kind, List<ImpactVm> Vms);
public record ActionImpact(List<ImpactMachine> Machines, int VmCount, int RunningVmCount);
