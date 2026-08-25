using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VMentory.Core.Estate;
using VMentory.Core.Persistence;

namespace VMentory.Web;

// First-run import of the estate (machines + the owner's remediation tracker) from the embedded
// wwwroot/estate-seed.json. Runs only into empty tables, so a redeploy never overwrites what the
// operator has since ticked, added or rescheduled. VMENTORY_ESTATE_SEED=0 disables it.
public static class EstateSeeder
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static async Task SeedAsync(VMentoryDbContext db, EstateState state, CancellationToken ct = default)
    {
        var raw = LoadEmbedded("estate-seed.json");
        if (raw == null) return;
        var seed = JsonSerializer.Deserialize<SeedFile>(raw, Json);
        if (seed == null) return;

        state.Facts = seed.Facts;
        if (Environment.GetEnvironmentVariable("VMENTORY_ESTATE_SEED") == "0") return;

        var now = DateTimeOffset.UtcNow;
        if (!await db.Machines.AnyAsync(ct))
        {
            foreach (var m in seed.Machines ?? [])
                db.Machines.Add(new MachineEntity
                {
                    Key = m.Key, Name = m.Name, Address = m.Address ?? "", Kind = m.Kind, Role = m.Role ?? "",
                    Model = m.Model ?? "", ServiceTag = m.ServiceTag, ManagementIp = m.ManagementIp, Notes = m.Notes,
                    SortOrder = m.SortOrder,
                    StaticVmsJson = JsonSerializer.Serialize(m.StaticVms ?? [], EstateState.Json),
                    StaticVmsVerifiedAt = m.StaticVmsVerifiedAt, StaticVmsSource = m.StaticVmsSource,
                });
            await db.SaveChangesAsync(ct);
            DevLog.Ok($"[ESTATE] seeded {seed.Machines?.Count ?? 0} machines");
        }

        if (!await db.Actions.AnyAsync(ct))
        {
            var byRef = new Dictionary<int, ActionItemEntity>();
            foreach (var a in seed.Actions ?? [])
            {
                var e = new ActionItemEntity
                {
                    Ref = a.Ref, Title = a.Title, Why = a.Why ?? "", How = a.How ?? "", DoneWhen = a.DoneWhen ?? "",
                    Priority = a.Priority, Class = a.Class, Group = a.Group, Status = a.Status,
                    Source = "seed", SourceKey = a.SourceKey,
                    AffectedMachinesJson = JsonSerializer.Serialize(a.AffectedMachines ?? []),
                    AffectedVmsJson = JsonSerializer.Serialize(a.AffectedVms ?? []),
                    PurchasesJson = JsonSerializer.Serialize(a.Purchases ?? []),
                    RequiresDowntime = a.RequiresDowntime,
                    CreatedAt = a.CreatedAt ?? now, UpdatedAt = now,
                    CompletedAt = a.Status == ActionStatus.Done ? (a.CompletedAt ?? now) : null,
                    CreatedBy = "seed",
                };
                foreach (var n in a.Notes ?? [])
                    e.Notes.Add(new ActionNoteEntity { At = n.At ?? now, By = n.By ?? "tracker", Text = n.Text });
                byRef[a.Ref] = e;
                db.Actions.Add(e);
            }
            foreach (var a in seed.Actions ?? [])
                foreach (var dep in a.DependsOn ?? [])
                    if (byRef.TryGetValue(dep, out var blocker) && dep != a.Ref)
                        db.ActionDependencies.Add(new ActionDependencyEntity { Blocked = byRef[a.Ref], Blocker = blocker });
            await db.SaveChangesAsync(ct);
            DevLog.Ok($"[ESTATE] seeded {seed.Actions?.Count ?? 0} actions");
        }
    }

    private static string? LoadEmbedded(string suffix)
    {
        var asm = Assembly.GetEntryAssembly()!;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ── Seed file shape ──────────────────────────────────────────────────────
    private sealed class SeedFile
    {
        public JsonElement? Facts { get; set; }
        public List<SeedMachine>? Machines { get; set; }
        public List<SeedAction>? Actions { get; set; }
    }
    private sealed class SeedMachine
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Address { get; set; }
        public MachineKind Kind { get; set; }
        public string? Role { get; set; }
        public string? Model { get; set; }
        public string? ServiceTag { get; set; }
        public string? ManagementIp { get; set; }
        public string? Notes { get; set; }
        public int SortOrder { get; set; }
        public List<StaticVm>? StaticVms { get; set; }
        public DateTimeOffset? StaticVmsVerifiedAt { get; set; }
        public string? StaticVmsSource { get; set; }
    }
    private sealed class SeedAction
    {
        public int Ref { get; set; }
        public string Title { get; set; } = "";
        public string? Why { get; set; }
        public string? How { get; set; }
        public string? DoneWhen { get; set; }
        public ActionPriority Priority { get; set; } = ActionPriority.Medium;
        public ActionClass Class { get; set; } = ActionClass.Remote;
        public string? Group { get; set; }
        public ActionStatus Status { get; set; } = ActionStatus.Open;
        public string? SourceKey { get; set; }
        public List<string>? AffectedMachines { get; set; }
        public List<string>? AffectedVms { get; set; }
        public List<string>? Purchases { get; set; }
        public bool RequiresDowntime { get; set; }
        public List<int>? DependsOn { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public List<SeedNote>? Notes { get; set; }
    }
    private sealed class SeedNote
    {
        public DateTimeOffset? At { get; set; }
        public string? By { get; set; }
        public string Text { get; set; } = "";
    }
}
