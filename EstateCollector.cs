using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VMentory.Core.Estate;
using VMentory.Core.Persistence;

namespace VMentory.Web;

// Latest hardware reading, held in memory so /api/estate never has to wait on the monitor.
// Loaded from the newest persisted snapshot at startup, replaced on every successful poll.
public sealed class EstateState
{
    private readonly object _lock = new();
    private HardwareSnapshot? _hardware;
    public HardwareSnapshot? Hardware { get { lock (_lock) return _hardware; } set { lock (_lock) _hardware = value; } }
    public bool MonitorConfigured { get; init; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public JsonElement? Facts { get; set; }   // free-form seed facts (backup coverage etc.) for the dashboard header

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task LoadLatestAsync(VMentoryDbContext db, CancellationToken ct = default)
    {
        var latest = await db.HardwareSnapshots.AsNoTracking().Where(s => s.Ok).OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        if (latest == null) return;
        try { Hardware = JsonSerializer.Deserialize<HardwareSnapshot>(latest.PayloadJson, Json); } catch { }
    }
}

// Polls the hardware monitor, persists the reading, turns faults into suggested actions.
// Deliberately not on the 30 s reachability cadence: OME answers slowly, and hardware faults do
// not change by the minute. Default 5 min, VMENTORY_OME_INTERVAL to override.
public sealed class EstateCollector(OmeOptions opt, EstateState state, EventHub hub, IServiceScopeFactory scopes, AppConfig config) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opt.Enabled || !config.Persist)
        {
            DevLog.Warn("[ESTATE] hardware monitor not configured (VMENTORY_OME_URL/USER/PASSWORD) — estate shows tracker + inventory only");
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await CollectOnceAsync(ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { state.LastError = ex.Message; DevLog.Err($"[ESTATE] collect failed: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(opt.IntervalSeconds), ct);
        }
    }

    // Also the target of POST /api/estate/refresh. Overlapping calls coalesce into one.
    public async Task<bool> CollectOnceAsync(CancellationToken ct)
    {
        if (!opt.Enabled || !config.Persist) return false;
        if (!await _gate.WaitAsync(0, ct)) return false;
        try
        {
            using var client = new OmeClient(opt);
            var snap = await client.ReadAsync(ct);
            state.LastAttempt = snap.TakenAt;

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            db.HardwareSnapshots.Add(new HardwareSnapshotEntity
            {
                TakenAt = snap.TakenAt, Source = snap.Source, Ok = snap.Ok, Error = snap.Error,
                PayloadJson = JsonSerializer.Serialize(snap, EstateState.Json),
            });
            await db.SaveChangesAsync(ct);

            if (!snap.Ok)
            {
                state.LastError = snap.Error;
                hub.Broadcast("estateUpdated", new { ok = false, error = snap.Error, takenAt = snap.TakenAt });
                return false;
            }

            state.Hardware = snap;
            state.LastError = null;
            var raised = await ActionSuggester.ApplyAsync(snap, db, ct);
            hub.Broadcast("estateUpdated", new { ok = true, takenAt = snap.TakenAt, devices = snap.Devices.Count, raised });
            if (raised > 0) hub.Broadcast("actionsChanged", new { reason = "collector", raised });
            return true;
        }
        finally { _gate.Release(); }
    }
}

// Turns a hardware fault into a suggested action, once. The dedupe handle is SourceKey =
// "ome:{serviceTag}:{fault.Key}"; a seeded tracker item may carry the same key so the collector
// adopts it instead of raising a twin. A fault that reappears after its action was ticked off
// reopens the action with a dated note — a replaced part that still flags is not done.
public static class ActionSuggester
{
    public static async Task<int> ApplyAsync(HardwareSnapshot snap, VMentoryDbContext db, CancellationToken ct)
    {
        var machines = await db.Machines.AsNoTracking().ToListAsync(ct);
        var byTag = machines.Where(m => !string.IsNullOrWhiteSpace(m.ServiceTag))
                            .ToDictionary(m => m.ServiceTag!.Trim().ToUpperInvariant(), m => m);
        var now = DateTimeOffset.UtcNow;
        var raised = 0;
        var nextRef = (await db.Actions.MaxAsync(a => (int?)a.Ref, ct) ?? 0) + 1;

        foreach (var dev in snap.Devices)
        {
            byTag.TryGetValue(dev.ServiceTag.Trim().ToUpperInvariant(), out var machine);
            var label = machine?.Name ?? dev.DeviceName;

            foreach (var fault in dev.Faults)
            {
                var key = $"ome:{dev.ServiceTag}:{fault.Key}";
                var existing = await db.Actions.Include(a => a.Notes).FirstOrDefaultAsync(a => a.SourceKey == key, ct);
                if (existing != null)
                {
                    existing.LastDetectedAt = now;
                    if (existing.Status == ActionStatus.Done && existing.CompletedAt is { } done && now - done > TimeSpan.FromHours(6))
                    {
                        existing.Status = ActionStatus.Open;
                        existing.UpdatedAt = now;
                        existing.Notes.Add(new ActionNoteEntity { At = now, By = "collector", Text = $"Reopened — the monitor still reports: {fault.Detail}" });
                    }
                    continue;
                }

                var s = Suggest(fault, label);
                if (s == null) continue;
                db.Actions.Add(new ActionItemEntity
                {
                    Ref = nextRef++,
                    Title = s.Value.Title, Why = s.Value.Why, How = s.Value.How, DoneWhen = s.Value.DoneWhen,
                    Priority = s.Value.Priority, Class = s.Value.Class, Group = "Hardware (from the monitor)",
                    Status = ActionStatus.Open, Source = "ome", SourceKey = key, LastDetectedAt = now,
                    AffectedMachinesJson = JsonSerializer.Serialize(machine != null ? new[] { machine.Key } : Array.Empty<string>()),
                    PurchasesJson = JsonSerializer.Serialize(s.Value.Purchases),
                    RequiresDowntime = s.Value.Downtime,
                    CreatedAt = now, UpdatedAt = now, CreatedBy = "collector",
                    Notes = [new ActionNoteEntity { At = now, By = "collector", Text = $"Raised from the hardware monitor: {fault.Component} — {fault.Detail}" }],
                });
                raised++;
            }
        }
        await db.SaveChangesAsync(ct);
        return raised;
    }

    private static (string Title, string Why, string How, string DoneWhen, ActionPriority Priority, ActionClass Class, string[] Purchases, bool Downtime)? Suggest(HardwareFault f, string machine)
    {
        if (f.Key.StartsWith("disk-predfail:"))
            return ($"Replace {machine} {f.Component.ToLowerInvariant()} — predictive failure",
                $"{f.Detail}. The array is still redundant but exposed: a second failure in it loses data.",
                "Hot-swap with a matching drive and let the array rebuild. Reseat is not a fix for a predictive flag.",
                "the monitor shows the bay Online with no predictive flag and the array has finished rebuilding",
                ActionPriority.High, ActionClass.Purchase, [$"Replacement drive matching: {f.Detail.Split('—')[0].Trim()}"], false);

        if (f.Key.StartsWith("psu:"))
            return ($"Replace {machine} {f.Component} — failed",
                $"{f.Detail}. A second power event takes the machine down.",
                "Reseat the unit first — a seating fault reads the same as a dead PSU. Replace it if it stays failed.",
                "both PSUs read Presence Detected and the machine's health clears to OK",
                ActionPriority.High, ActionClass.Purchase, [$"Power supply: {Between(f.Detail, '(', ')')}"], false);

        if (f.Key == "disk-foreign")
            return ($"Decide keep-or-clear on {machine}'s foreign disks",
                $"{f.Detail}. Foreign disks hold unknown content and hold the Storage subsystem at Warning.",
                "Identify what the disks carried before clearing anything — do not clear a foreign config blindly. Record the decision, then import or clear.",
                "no Foreign disks remain and the Storage subsystem clears",
                ActionPriority.Medium, ActionClass.OnSite, [], false);

        if (f.Key.StartsWith("raid-controller:"))
            return ($"Investigate {machine} {f.Component} warning — replace the write-cache battery if degraded",
                $"{f.Detail}.",
                "Read the controller's battery state from the iDRAC. A degraded BBU is a replaceable part; the swap needs the machine powered off.",
                "the controller reads OK and write-back caching is restored",
                ActionPriority.Medium, ActionClass.Purchase, ["PERC RAID controller battery (BBU)"], true);

        if (f.Key == "unreachable")
            return ($"Restore the monitor's contact with {machine}'s iDRAC",
                f.Detail + ".",
                "Check the iDRAC answers on its address and that the monitor's stored credential is still valid.",
                "the device reconnects in the monitor and reports health",
                ActionPriority.High, ActionClass.Remote, [], false);

        if (f.Key.StartsWith("disk-status:") || f.Key.StartsWith("subsystem:"))
            return ($"Investigate {machine} {f.Component} — {f.Severity}",
                f.Detail + ".",
                "Read the component detail in the monitor and on the iDRAC; decide whether it is a part, a seating fault or noise.",
                "the component reads OK or the finding is recorded as accepted",
                f.Severity == FaultSeverity.Critical ? ActionPriority.High : ActionPriority.Medium, ActionClass.Remote, [], false);

        return null;
    }

    private static string Between(string s, char a, char b)
    {
        var i = s.IndexOf(a); var j = i >= 0 ? s.IndexOf(b, i + 1) : -1;
        return i >= 0 && j > i ? s[(i + 1)..j] : s;
    }
}
