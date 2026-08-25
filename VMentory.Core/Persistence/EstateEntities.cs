using VMentory.Core.Estate;

namespace VMentory.Core.Persistence;

// ── ENG-0015: estate + remediation tracker ────────────────────────────────────
// Single-operator, same as everything else here: no tenant_id. The estate is "the one this
// deployment watches"; a second client is a second deployment.

// A physical machine the operator is responsible for. Identity for the joins:
//   ServiceTag → the hardware monitor's device;  Address → a registered hypervisor Host.
public class MachineEntity
{
    public string Key { get; set; } = "";             // stable slug, e.g. "atlas"
    public string Name { get; set; } = "";            // display, e.g. "Atlas[103]"
    public string Address { get; set; } = "";
    public MachineKind Kind { get; set; } = MachineKind.HyperV;
    public string Role { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ServiceTag { get; set; }
    public string? ManagementIp { get; set; }          // iDRAC / IPMI
    public string? Notes { get; set; }
    public int SortOrder { get; set; }
    // Guests recorded by hand for machines with no live inventory (JSON List<StaticVm>).
    public string StaticVmsJson { get; set; } = "[]";
    public DateTimeOffset? StaticVmsVerifiedAt { get; set; }
    public string? StaticVmsSource { get; set; }
}

public class ActionItemEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    // The number a human calls it by. Seeded items keep their tracker number; new ones take the
    // next free integer so "item 41" means the same thing in a chat, a visit checklist and the UI.
    public int Ref { get; set; }
    public string Title { get; set; } = "";
    public string Why { get; set; } = "";
    public string How { get; set; } = "";
    public string DoneWhen { get; set; } = "";
    public ActionPriority Priority { get; set; } = ActionPriority.Medium;
    public ActionClass Class { get; set; } = ActionClass.Remote;
    public string? Group { get; set; }                 // batching label: "Rhea evacuation"
    public ActionStatus Status { get; set; } = ActionStatus.Open;
    // Provenance. "seed" = imported from the owner's tracker, "ome" = raised by the hardware
    // collector, "manual" = created in the UI. SourceKey is the dedupe handle for collector-raised
    // items (mirrors HardwareFault.Key); a seeded item may carry one so the collector adopts it.
    public string Source { get; set; } = "manual";
    public string? SourceKey { get; set; }
    public DateTimeOffset? LastDetectedAt { get; set; }
    // JSON string lists. Machine keys, extra VM names beyond the machines' own guests, parts.
    public string AffectedMachinesJson { get; set; } = "[]";
    public string AffectedVmsJson { get; set; } = "[]";
    public string PurchasesJson { get; set; } = "[]";
    public bool RequiresDowntime { get; set; }
    public DateTimeOffset? ScheduledStart { get; set; }
    public DateTimeOffset? ScheduledEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? CompletedBy { get; set; }

    public List<ActionNoteEntity> Notes { get; set; } = [];
    public List<ActionDependencyEntity> BlockedBy { get; set; } = [];   // this item waits on Blocker
    public List<ActionDependencyEntity> Blocks { get; set; } = [];      // this item is a Blocker
}

// Directed edge: Blocked waits for Blocker. Cycles are rejected at the API.
public class ActionDependencyEntity
{
    public string BlockedId { get; set; } = "";
    public string BlockerId { get; set; } = "";
    public ActionItemEntity? Blocked { get; set; }
    public ActionItemEntity? Blocker { get; set; }
}

// Dated note under an item — the "add a dated note under anything you change" rule, enforced by
// shape: there is no free-text status field to overwrite, only notes to append.
public class ActionNoteEntity
{
    public long Id { get; set; }
    public string ActionId { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? By { get; set; }
    public string Text { get; set; } = "";
    public ActionItemEntity? Action { get; set; }
}

// Append-only hardware readings (JSON HardwareSnapshot). Order by Id, not TakenAt — gotcha #8a.
public class HardwareSnapshotEntity
{
    public long Id { get; set; }
    public DateTimeOffset TakenAt { get; set; }
    public string Source { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string PayloadJson { get; set; } = "";
}
