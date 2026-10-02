using Microsoft.EntityFrameworkCore;
using VMentory.Core.Persistence;

namespace VMentory.Web.Fleet;

// One feed of everything VMentory has set in motion — migrations (persisted jobs), power actions
// (ActivityRegistry) and drains — for the activity strip and the Actions page. Server-side, so every
// session and screen sees the same thing, and a closed dialog loses nothing.
public static class ActivityEndpoints
{
    public sealed record Item(string Kind, string Id, string Title, string Status, bool Active, string? Phase,
        string? Guest, string? Node, string? Target, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
        string? By, string? Error, string? DrainPlanId);

    public static void MapActivityEndpoints(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AppConfig>().Persist) return;

        app.MapGet("/api/activity", async (Deps d, ActivityRegistry reg) =>
        {
            var db = d.Db();
            // all filtering by time in memory — SQLite cannot compare DateTimeOffset (gotcha #8a)
            var jobs = (await db.MigrationJobs.AsNoTracking()
                    .Where(j => j.Status != MigrationJobStatus.Planned && j.Status != MigrationJobStatus.Skipped).ToListAsync())
                .OrderByDescending(j => j.QueuedAt ?? j.CreatedAt).Take(60).ToList();
            var queue = jobs.Where(j => j.Status == MigrationJobStatus.Queued).OrderBy(j => j.QueuedAt).ThenBy(j => j.Seq).Select(j => j.Id).ToList();
            var drains = (await db.DrainPlans.AsNoTracking().Where(p => p.Status == DrainPlanStatus.Running).ToListAsync());

            var items = new List<Item>();
            items.AddRange(jobs.Select(j => new Item("migrate", j.Id, $"Migrate {j.GuestName} → {j.TargetNode}",
                Status(j.Status), j.Status is MigrationJobStatus.Queued or MigrationJobStatus.Running,
                j.Status == MigrationJobStatus.Queued ? $"queued — {queue.IndexOf(j.Id) + 1} of {queue.Count} (one move at a time)" : j.Phase,
                j.GuestName, j.SourceNode, j.TargetNode, j.StartedAt ?? j.QueuedAt ?? j.CreatedAt, j.FinishedAt, j.CreatedBy, j.Error, j.DrainPlanId)));
            items.AddRange(reg.All.Select(t => new Item("power", t.Id, $"{Cap(t.Action)} {t.Guest}",
                t.Running ? "running" : t.ExitStatus == "OK" ? "ok" : "failed", t.Running,
                t.Running ? $"{t.Action} sent to {t.Node}" : t.ExitStatus == "OK" ? "finished" : t.ExitStatus,
                t.Guest, t.Node, null, t.StartedAt, t.FinishedAt, t.By, t.Running || t.ExitStatus == "OK" ? null : t.ExitStatus, null)));
            items.AddRange(drains.Select(p => new Item("drain", p.Id, $"Clear host {p.SourceNode}", p.AbortRequested ? "aborting" : "running", true,
                null, null, p.SourceNode, null, p.ApprovedAt, null, p.ApprovedBy, null, p.Id)));

            var ordered = items.OrderByDescending(i => i.Active).ThenByDescending(i => i.StartedAt).ToList();
            return Results.Ok(new { now = DateTimeOffset.UtcNow, active = ordered.Count(i => i.Active), items = ordered });
        });

        // detail + Proxmox task log of one power action
        app.MapGet("/api/activity/power/{id}", async (string id, Deps d, ActivityRegistry reg, HttpContext ctx) =>
        {
            var t = reg.Get(id);
            if (t == null) return Results.NotFound(new { error = "unknown or expired (power actions are kept in memory, last 100)" });
            string? log = null;
            var h = d.Store.GetHost(t.HostId);
            var tok = h != null ? FleetCollector.ReadToken(d.Store, h) : null;
            if (h != null && tok != null)
            {
                try
                {
                    using var pve = new PveClient(h.Address, tok, h.SkipTlsVerification);
                    var l = await pve.GetAsync($"/nodes/{PveClient.Enc(t.Node)}/tasks/{PveClient.Enc(t.Upid)}/log?limit=500", ctx.RequestAborted);
                    log = string.Join('\n', Pj.Arr(l).Select(x => Pj.Str(x, "t")));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log = "task log not readable: " + ex.Message; }
            }
            return Results.Ok(new
            {
                t.Id, t.Upid, t.HostId, t.Node, t.Guest, t.GuestType, t.Vmid, t.Action, t.By, t.StartedAt, t.FinishedAt, t.ExitStatus,
                running = t.Running, log,
            });
        });
    }

    private static string Status(MigrationJobStatus s) => s switch
    {
        MigrationJobStatus.Queued => "queued", MigrationJobStatus.Running => "running", MigrationJobStatus.Succeeded => "ok",
        MigrationJobStatus.Failed => "failed", MigrationJobStatus.Cancelled => "cancelled", _ => s.ToString().ToLowerInvariant(),
    };
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
