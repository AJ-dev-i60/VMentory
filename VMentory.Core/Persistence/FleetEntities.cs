using System.Text.Json.Serialization;

namespace VMentory.Core.Persistence;

// ── ENG-0016: fleet operations ────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FleetRuleKind
{
    AntiAffinity,   // members never share a host
    Pinned,         // never two copies; offline moves only
    Ephemeral,      // disposable — a deletion candidate, not something to migrate
    SelfHost,       // runs this app (or what it depends on): moved last in a drain, warned on any move
}

// A constraint the operator has stated about guests. Members are guest *names* (case-insensitive),
// because VMIDs change on a move and the rule must survive it.
public class FleetRuleEntity
{
    public long Id { get; set; }
    public FleetRuleKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string MembersJson { get; set; } = "[]";
    public string? Note { get; set; }
    public string Source { get; set; } = "manual";   // seed | manual
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

// A node taken out of suggestions. While active its hardware faults do not raise actions.
public class NodeMaintenanceEntity
{
    public string HostId { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset Until { get; set; }
    public string Reason { get; set; } = "";
    public string? By { get; set; }
    public string? DrainPlanId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationJobStatus { Planned, Queued, Running, Succeeded, Failed, Cancelled, Skipped }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationMode { Online, Offline, Restart }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DrainRowAction { Move, Stay }

// One guest move. Standalone (DrainPlanId null) or a row of a drain plan. A Planned row is a plan
// line the operator can still edit; execution flips it to Queued and the runner takes it from there.
public class MigrationJobEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string? DrainPlanId { get; set; }
    public int Seq { get; set; }
    public DrainRowAction RowAction { get; set; } = DrainRowAction.Move;
    public string? PlanNote { get; set; }             // why a row cannot be placed, or why it stays

    public string GuestType { get; set; } = "qemu";
    public int Vmid { get; set; }
    public string GuestName { get; set; } = "";
    public bool WasRunning { get; set; }
    public string SourceHostId { get; set; } = "";
    public string SourceNode { get; set; } = "";
    public string? TargetHostId { get; set; }
    public string? TargetNode { get; set; }
    public int? TargetVmid { get; set; }
    public string? TargetStorage { get; set; }
    public MigrationMode Mode { get; set; } = MigrationMode.Offline;

    public long? BytesPlanned { get; set; }           // allocated bytes at plan/preflight time; null = unknown
    public long? BytesDone { get; set; }              // last figure the task log reported
    public MigrationJobStatus Status { get; set; } = MigrationJobStatus.Queued;
    public string? Phase { get; set; }
    public string? Upid { get; set; }
    public string? Error { get; set; }
    public string? LogTail { get; set; }
    public string? PreflightJson { get; set; }

    public bool TargetVmidWasFree { get; set; }
    public bool PartialOnTarget { get; set; }
    public bool CleanupDone { get; set; }
    public bool SourceRestarted { get; set; }
    public bool CancelRequested { get; set; }
    // operator's explicit choice: if a clean shutdown times out, power the guest off instead of failing
    public bool ForceStopOnTimeout { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? TransferStartedAt { get; set; }
    public DateTimeOffset? TransferEndedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DrainPlanStatus { Draft, Running, Completed, Aborted, Failed, Discarded }

public class DrainPlanEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string SourceHostId { get; set; } = "";
    public string SourceNode { get; set; } = "";
    public DrainPlanStatus Status { get; set; } = DrainPlanStatus.Draft;
    public string? ReverseOfPlanId { get; set; }       // set on a "return to service" plan
    public string? ActionId { get; set; }              // the remediation item this drain serves
    public DateTimeOffset? MaintenanceUntil { get; set; }
    public string? MaintenanceReason { get; set; }
    public bool AbortRequested { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ReportJson { get; set; }
}
