using System.Collections.Concurrent;

namespace VMentory.Web.Fleet;

// Power tasks (start/shutdown/stop/reboot) sent from the UI, held server-side so every session sees
// what is underway — the activity strip and the Actions page. Migration jobs and drains are already
// persisted (MigrationJobEntity / DrainPlanEntity); power tasks are seconds-to-minutes long, so memory
// is enough. Keeps the most recent 100.
public sealed class ActivityRegistry
{
    public sealed class PowerTask
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
        public required string Upid { get; init; }
        public required string HostId { get; init; }
        public required string Node { get; init; }
        public required string Guest { get; init; }
        public required string GuestType { get; init; }
        public int Vmid { get; init; }
        public required string Action { get; init; }
        public string? By { get; init; }
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
        public string? ExitStatus { get; set; }
        public bool Running => FinishedAt == null;
    }

    private readonly ConcurrentDictionary<string, PowerTask> _tasks = new();

    public PowerTask Add(PowerTask t)
    {
        _tasks[t.Id] = t;
        foreach (var old in _tasks.Values.OrderByDescending(x => x.StartedAt).Skip(100).ToList()) _tasks.TryRemove(old.Id, out _);
        return t;
    }

    public PowerTask? Get(string id) => _tasks.GetValueOrDefault(id);
    public IReadOnlyList<PowerTask> All => [.. _tasks.Values.OrderByDescending(t => t.StartedAt)];
}
