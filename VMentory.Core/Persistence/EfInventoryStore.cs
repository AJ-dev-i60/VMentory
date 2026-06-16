using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace VMentory.Core.Persistence;

// EF Core implementation of the registry + snapshot store. Scoped: one per request/operation scope.
public class EfInventoryStore(VMentoryDbContext db) : IInventoryStore
{
    private static readonly JsonSerializerOptions PayloadJson = new() { WriteIndented = false };

    public async Task<List<Host>> LoadRegistryAsync(CancellationToken ct = default)
    {
        var regs = await db.Hosts.AsNoTracking().ToListAsync(ct);
        var hosts = new List<Host>(regs.Count);

        foreach (var reg in regs)
        {
            var host = new Host
            {
                Id = reg.Id,
                Platform = reg.Platform,
                Address = reg.Address,
                UseGlobalCreds = reg.UseGlobalCreds,
            };

            var latest = await db.Snapshots.AsNoTracking()
                .Where(s => s.HostId == reg.Id)
                .OrderByDescending(s => s.Id)   // autoincrement PK: higher = newer (SQLite can't ORDER BY DateTimeOffset)
                .FirstOrDefaultAsync(ct);

            if (latest != null) ApplySnapshot(host, latest.PayloadJson);
            hosts.Add(host);
        }

        return hosts;
    }

    public async Task UpsertRegistrationAsync(Host host, CancellationToken ct = default)
    {
        var existing = await db.Hosts.FirstOrDefaultAsync(h => h.Id == host.Id, ct);
        if (existing == null)
        {
            db.Hosts.Add(new HostRegistrationEntity
            {
                Id = host.Id,
                Platform = host.Platform,
                Address = host.Address,
                UseGlobalCreds = host.UseGlobalCreds,
                AddedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Platform = host.Platform;
            existing.Address = host.Address;
            existing.UseGlobalCreds = host.UseGlobalCreds;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string hostId, CancellationToken ct = default)
    {
        var existing = await db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId, ct);
        if (existing == null) return;
        db.Hosts.Remove(existing);   // cascades to snapshots
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveSnapshotAsync(Host host, CancellationToken ct = default)
    {
        db.Snapshots.Add(new InventorySnapshotEntity
        {
            HostId = host.Id,
            TakenAt = DateTimeOffset.UtcNow,
            PayloadJson = ToSnapshotPayload(host),
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<Host>> GetLatestSnapshotHostsAsync(IEnumerable<string> hostIds, CancellationToken ct = default)
    {
        var result = new List<Host>();
        foreach (var id in hostIds.Distinct())
        {
            var latest = await db.Snapshots.AsNoTracking()
                .Where(s => s.HostId == id)
                .OrderByDescending(s => s.Id)   // autoincrement PK: higher = newer (SQLite can't ORDER BY DateTimeOffset)
                .FirstOrDefaultAsync(ct);
            if (latest == null) continue;

            var host = new Host { Id = id };
            ApplySnapshot(host, latest.PayloadJson);
            result.Add(host);
        }
        return result;
    }

    // ── Snapshot payload mapping (single place for the creds/runtime exclusion) ──────────────

    // Host.PerHostCreds is [JsonIgnore], so secrets never enter the payload.
    private static string ToSnapshotPayload(Host host) => JsonSerializer.Serialize(host, PayloadJson);

    // Copy only the inventory fields back onto the target; runtime/UI fields (Reachability,
    // ScanState, AddError, Connecting) keep their fresh defaults.
    private static void ApplySnapshot(Host target, string payloadJson)
    {
        var s = JsonSerializer.Deserialize<Host>(payloadJson, PayloadJson);
        if (s == null) return;

        target.Platform = s.Platform;
        target.Fqdn = s.Fqdn;
        target.OsCaption = s.OsCaption;
        target.OsVersion = s.OsVersion;
        target.Manufacturer = s.Manufacturer;
        target.Model = s.Model;
        target.Serial = s.Serial;
        target.LastBoot = s.LastBoot;
        target.CpuModel = s.CpuModel;
        target.SocketCount = s.SocketCount;
        target.TotalCores = s.TotalCores;
        target.TotalLogicalProcs = s.TotalLogicalProcs;
        target.TotalRamGb = s.TotalRamGb;
        target.Volumes = s.Volumes;
        target.Vms = s.Vms;
        target.LastScanned = s.LastScanned;
    }
}
