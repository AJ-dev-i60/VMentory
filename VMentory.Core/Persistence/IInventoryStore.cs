namespace VMentory.Core.Persistence;

// Durable host registry + inventory snapshots. The in-memory Store stays the working set; this is
// the write-through/load side-channel. Reconstructs the existing combined Host (registration +
// latest snapshot) so the API/SPA shape is unchanged.
public interface IInventoryStore
{
    // Rebuild the registered hosts (identity + last-known inventory) at startup. Runtime fields
    // (reachability/scan state) default; credentials are null (not persisted).
    Task<List<Host>> LoadRegistryAsync(CancellationToken ct = default);

    Task UpsertRegistrationAsync(Host host, CancellationToken ct = default);
    Task RemoveAsync(string hostId, CancellationToken ct = default);

    // Persist a snapshot of the host's current inventory.
    Task SaveSnapshotAsync(Host host, CancellationToken ct = default);

    // Latest persisted snapshot per host, as Host objects — the "previous" input for the diff.
    Task<List<Host>> GetLatestSnapshotHostsAsync(IEnumerable<string> hostIds, CancellationToken ct = default);
}
