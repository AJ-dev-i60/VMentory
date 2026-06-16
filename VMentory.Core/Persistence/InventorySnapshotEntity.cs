namespace VMentory.Core.Persistence;

// One inventory reading for a host at an instant — the historical record Phase 1 never kept.
// PayloadJson is the scanned Host inventory (hardware/OS/Volumes/Vms) serialized; it excludes
// credentials (Host.PerHostCreds is [JsonIgnore]) and is read back to populate a Host on load and
// to feed the diff (latest two snapshots).
public class InventorySnapshotEntity
{
    public long Id { get; set; }
    public string HostId { get; set; } = "";
    public DateTimeOffset TakenAt { get; set; }
    public string PayloadJson { get; set; } = "";

    public HostRegistrationEntity? Host { get; set; }
}
