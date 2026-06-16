namespace VMentory.Core.Persistence;

// Durable registry identity for a host the operator added — the minimum needed to reconstruct a
// Host on restart. Inventory data (hardware/OS/VMs) is NOT here; it comes from the latest
// InventorySnapshot. Credentials and runtime/scan state are deliberately NOT persisted
// (secrets wait for ISecretStore; runtime state is transient). Single-operator: no tenant_id.
public class HostRegistrationEntity
{
    public string Id { get; set; } = "";
    public PlatformKind Platform { get; set; } = PlatformKind.HyperV;
    public string Address { get; set; } = "";
    public bool UseGlobalCreds { get; set; } = true;
    public DateTimeOffset AddedAt { get; set; }

    public List<InventorySnapshotEntity> Snapshots { get; set; } = [];
}
