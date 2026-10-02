using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VMentory.Core.Auth;
using VMentory.Core.Estate;
using VMentory.Core.Fleet;
using VMentory.Core.Persistence;

namespace VMentory.Web.Fleet;

// ENG-0016 — the Proxmox fleet: see, rank, move, drain.
// Reads need a session. MigrateGuest gates single moves; DrainHost gates drains, maintenance and rules.
public static class FleetEndpoints
{
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void MapFleetEndpoints(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AppConfig>().Persist)
        {
            app.MapGet("/api/fleet", () => Results.Ok(new { nodes = Array.Empty<object>(), guests = Array.Empty<object>(), mock = true }));
            return;
        }

        app.MapGet("/api/fleet", async (Deps d) =>
        {
            var (db, f) = await d.ContextAsync();
            var jobs = await db.MigrationJobs.AsNoTracking().Where(j => j.Status != MigrationJobStatus.Planned).ToListAsync();
            var drains = await db.DrainPlans.AsNoTracking().Where(p => p.Status != DrainPlanStatus.Discarded).ToListAsync();
            return Results.Json(new
            {
                lastCycle = d.State.LastCycle,
                config = new
                {
                    intervalSeconds = d.Opt.IntervalSeconds, probeEnabled = d.Opt.ProbeEnabled, probeIntervalSeconds = d.Opt.ProbeIntervalSeconds,
                    writeTokens = f.Nodes.Count(n => n.HasMigrateToken),
                    hardwareMonitor = d.Estate.MonitorConfigured, hardwareTakenAt = d.Estate.Hardware?.TakenAt,
                },
                policy = FleetPolicy.Describe(d.Opt),
                rate = new { bytesPerSec = f.RateBytesPerSec, samples = f.RateSamples },
                nodes = f.Nodes.Select(n => (n, s: FleetAnalysis.Score(n, f)))
                               .OrderBy(x => x.s.Vetoes.Count > 0 ? 1 : 0).ThenByDescending(x => x.s.Score ?? -1)
                               .Select(x => NodeDto(x.n, f, x.s)).ToList(),
                guests = FleetAnalysis.AllGuests(f).Select(p => GuestDto(p.Node, p.Guest, f)).ToList(),
                findings = FleetAnalysis.Findings(f),
                rules = f.Rules.Select(r => new { r.Id, r.Kind, r.Name, members = FleetAnalysis.Members(r), r.Note, r.Source, r.CreatedAt, r.CreatedBy }),
                jobs = jobs.OrderByDescending(j => j.CreatedAt).Take(40).Select(JobDto).ToList(),
                drains = drains.OrderByDescending(p => p.CreatedAt).Take(10).Select(p => new { p.Id, p.SourceHostId, p.SourceNode, p.Status, p.CreatedAt, p.CreatedBy, p.FinishedAt, p.ReverseOfPlanId, p.ActionId }).ToList(),
            }, J);
        });

        app.MapPost("/api/fleet/refresh", async (Deps d, HttpContext ctx) =>
        {
            var ran = await d.Collector.CollectOnceAsync(ctx.RequestAborted, forceProbe: true);
            return Results.Ok(new { ok = ran, message = ran ? "Fleet re-read" : "A read is already running" });
        });

        // ── one guest ────────────────────────────────────────────────────────
        app.MapGet("/api/fleet/guests/{hostId}/{type}/{vmid:int}/targets", async (string hostId, string type, int vmid, Deps d) =>
        {
            var (_, f) = await d.ContextAsync();
            var (src, g) = Find(f, hostId, type, vmid);
            if (src == null || g == null) return Results.NotFound(new { error = "guest not in the latest reading" });
            return Results.Json(new { guest = GuestDto(src, g, f), targets = FleetAnalysis.RankTargets(f, src, g) }, J);
        });

        app.MapPost("/api/fleet/preflight", async (Deps d, HttpContext ctx) =>
        {
            var b = await Body<MoveDto>(ctx);
            if (b == null) return Results.BadRequest(new { error = "body required" });
            var (db, f) = await d.ContextAsync();
            var (src, g) = Find(f, b.HostId, b.Type, b.Vmid);
            var tgt = f.ById(b.TargetHostId ?? "");
            if (src == null || g == null || tgt == null) return Results.NotFound(new { error = "guest or target not found" });
            var pf = await FleetPreflight.RunAsync(f, src, g, tgt, b.Storage, b.Mode, d.Opt, d.Store, await MigrationRunner.ActionVmsAsync(db, ctx.RequestAborted), ctx.RequestAborted);
            return Results.Json(pf, J);
        });

        // Dry run by default. Execution needs dryRun:false, the exact confirm phrase "<guest> to <node>",
        // and — when the preflight raised warnings — acknowledgeWarnings:true.
        app.MapPost("/api/fleet/migrations", async (Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.MigrateGuest)) return Forbid("moving guests");
            var b = await Body<MoveDto>(ctx);
            if (b == null) return Results.BadRequest(new { error = "body required" });
            var (db, f) = await d.ContextAsync();
            var (src, g) = Find(f, b.HostId, b.Type, b.Vmid);
            var tgt = f.ById(b.TargetHostId ?? "");
            if (src == null || g == null || tgt == null) return Results.NotFound(new { error = "guest or target not found" });
            var pf = await FleetPreflight.RunAsync(f, src, g, tgt, b.Storage, b.Mode, d.Opt, d.Store, await MigrationRunner.ActionVmsAsync(db, ctx.RequestAborted), ctx.RequestAborted);
            if (b.DryRun != false) return Results.Json(new { dryRun = true, preflight = pf }, J);

            if (!pf.CanExecute) return Results.Json(new { error = "preflight has blockers", preflight = pf }, J, statusCode: 400);
            if (!string.Equals(b.Confirm?.Trim(), pf.ConfirmPhrase, StringComparison.Ordinal))
                return Results.Json(new { error = $"type exactly: {pf.ConfirmPhrase}", preflight = pf }, J, statusCode: 400);
            if (pf.Warnings.Count > 0 && b.AcknowledgeWarnings != true)
                return Results.Json(new { error = "acknowledge the warnings to proceed", preflight = pf }, J, statusCode: 400);
            if (await db.MigrationJobs.AnyAsync(j => j.SourceHostId == src.Host.Id && j.Vmid == g.Vmid && j.GuestType == g.Type
                                                     && (j.Status == MigrationJobStatus.Queued || j.Status == MigrationJobStatus.Running)))
                return Results.Json(new { error = "this guest already has a move queued or running" }, statusCode: 409);

            var now = DateTimeOffset.UtcNow;
            var job = new MigrationJobEntity
            {
                GuestType = g.Type, Vmid = g.Vmid, GuestName = g.Name, WasRunning = g.Running,
                SourceHostId = src.Host.Id, SourceNode = src.Name, TargetHostId = tgt.Host.Id, TargetNode = tgt.Name,
                TargetVmid = pf.TargetVmid, TargetStorage = pf.TargetStorage, Mode = pf.Mode, BytesPlanned = pf.BytesAllocated,
                Status = MigrationJobStatus.Queued, Phase = "queued", PreflightJson = JsonSerializer.Serialize(pf, J),
                CreatedAt = now, QueuedAt = now, CreatedBy = User(ctx),
            };
            db.MigrationJobs.Add(job);
            await db.SaveChangesAsync();
            await Audit(d, ctx, "fleet.migrate.requested", job.Id, new { job.GuestName, job.Vmid, from = job.SourceNode, to = job.TargetNode, mode = job.Mode.ToString(), bytes = job.BytesPlanned, warnings = pf.Warnings });
            d.Hub.Broadcast("fleetJob", new { job.Id, job.Status });
            return Results.Json(new { ok = true, job = JobDto(job) }, J);
        });

        app.MapGet("/api/fleet/jobs/{id}", async (string id, Deps d) =>
        {
            var db = d.Db();
            var j = await db.MigrationJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return j == null ? Results.NotFound() : Results.Json(new { job = JobDto(j), log = j.LogTail, preflight = j.PreflightJson == null ? (JsonElement?)null : JsonDocument.Parse(j.PreflightJson).RootElement }, J);
        });

        app.MapPost("/api/fleet/jobs/{id}/cancel", async (string id, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.MigrateGuest)) return Forbid("moving guests");
            var db = d.Db();
            var j = await db.MigrationJobs.FirstOrDefaultAsync(x => x.Id == id);
            if (j == null) return Results.NotFound();
            if (j.Status == MigrationJobStatus.Queued) { j.Status = MigrationJobStatus.Cancelled; j.Phase = "cancelled before it started"; j.FinishedAt = DateTimeOffset.UtcNow; }
            else if (j.Status == MigrationJobStatus.Running) j.CancelRequested = true;
            else return Results.BadRequest(new { error = $"job is {j.Status}" });
            await db.SaveChangesAsync();
            if (j.DrainPlanId != null) await MigrationRunner.FinishPlanIfDoneAsync(db, j.DrainPlanId, ctx.RequestAborted);
            await Audit(d, ctx, "fleet.migrate.cancel", j.Id, new { j.GuestName, j.Status });
            return Results.Ok(new { ok = true, status = j.Status.ToString() });
        });

        // Remove what a failed move left on the target — only a guest this job itself created (the
        // VMID was free before the job), never anything else.
        app.MapPost("/api/fleet/jobs/{id}/cleanup", async (string id, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.MigrateGuest)) return Forbid("moving guests");
            var db = d.Db();
            var j = await db.MigrationJobs.FirstOrDefaultAsync(x => x.Id == id);
            if (j == null) return Results.NotFound();
            if (j.Status is not (MigrationJobStatus.Failed or MigrationJobStatus.Cancelled) || !j.PartialOnTarget || !j.TargetVmidWasFree || j.CleanupDone)
                return Results.BadRequest(new { error = "nothing this job created is recorded on the target" });
            var tgt = d.Store.GetHost(j.TargetHostId!);
            var tok = tgt != null ? d.Opt.MigrateTokenFor(tgt.Address) : null;
            if (tgt == null || tok == null) return Results.BadRequest(new { error = "no write token for the target" });
            using var pve = new PveClient(tgt.Address, tok, tgt.SkipTlsVerification, TimeSpan.FromSeconds(60));
            try
            {
                await pve.DeleteAsync($"/nodes/{PveClient.Enc(j.TargetNode!)}/{j.GuestType}/{j.TargetVmid}?purge=1&destroy-unreferenced-disks=1", ctx.RequestAborted);
                j.CleanupDone = true; await db.SaveChangesAsync();
                await Audit(d, ctx, "fleet.migrate.cleanup", j.Id, new { j.TargetNode, j.TargetVmid });
                return Results.Ok(new { ok = true, message = $"removed {j.GuestType} {j.TargetVmid} from {j.TargetNode}" });
            }
            catch (PveException ex)
            {
                return Results.Json(new { error = ex.Message, hint = $"if it is locked: on {j.TargetNode} run 'qm unlock {j.TargetVmid}' (or pct) and retry, or destroy it by hand" }, statusCode: 502);
            }
        });

        // ── node maintenance ─────────────────────────────────────────────────
        app.MapPost("/api/fleet/nodes/{hostId}/maintenance", async (string hostId, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("node maintenance");
            var b = await Body<MaintenanceDto>(ctx);
            if (b == null) return Results.BadRequest(new { error = "body required" });
            var db = d.Db();
            var cur = await db.NodeMaintenance.FirstOrDefaultAsync(m => m.HostId == hostId);
            if (!b.On)
            {
                if (cur != null) db.NodeMaintenance.Remove(cur);
            }
            else
            {
                if (b.Until == null || b.Until <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "an expiry in the future is required" });
                if (string.IsNullOrWhiteSpace(b.Reason)) return Results.BadRequest(new { error = "a reason is required" });
                if (cur == null) db.NodeMaintenance.Add(cur = new NodeMaintenanceEntity { HostId = hostId, Since = DateTimeOffset.UtcNow });
                cur.Until = b.Until.Value; cur.Reason = b.Reason.Trim(); cur.By = User(ctx);
            }
            await db.SaveChangesAsync();
            await Audit(d, ctx, b.On ? "fleet.maintenance.on" : "fleet.maintenance.off", hostId, new { b.Reason, b.Until });
            d.Hub.Broadcast("fleetUpdated", new { reason = "maintenance" });
            return Results.Ok(new { ok = true });
        });

        // ── drain ────────────────────────────────────────────────────────────
        app.MapPost("/api/fleet/nodes/{hostId}/drain", async (string hostId, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("draining hosts");
            var b = await Body<DrainCreateDto>(ctx) ?? new DrainCreateDto();
            var (db, f) = await d.ContextAsync();
            var src = f.ById(hostId);
            if (src?.Reading == null) return Results.BadRequest(new { error = "no reading of this node to plan from" });
            if (await db.DrainPlans.AnyAsync(p => p.SourceHostId == hostId && (p.Status == DrainPlanStatus.Draft || p.Status == DrainPlanStatus.Running)))
                return Results.Json(new { error = "this node already has an open drain plan" }, statusCode: 409);
            var plan = new DrainPlanEntity { SourceHostId = hostId, SourceNode = src.Name, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = User(ctx), ActionId = b.ActionId };
            db.DrainPlans.Add(plan);
            db.MigrationJobs.AddRange(PlanRows(f, src, plan, src.Reading.Guests.Where(g => !g.Template).ToList(), User(ctx)));
            await db.SaveChangesAsync();
            await Audit(d, ctx, "fleet.drain.planned", plan.Id, new { node = src.Name });
            return Results.Json(await PlanDto(db, f, plan.Id), J);
        });

        app.MapGet("/api/fleet/drains/{id}", async (string id, Deps d) =>
        {
            var (db, f) = await d.ContextAsync();
            var dto = await PlanDto(db, f, id);
            return dto == null ? Results.NotFound() : Results.Json(dto, J);
        });

        app.MapMethods("/api/fleet/drains/{id}/rows/{jobId}", ["PATCH"], async (string id, string jobId, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("draining hosts");
            var b = await Body<RowDto>(ctx);
            if (b == null) return Results.BadRequest(new { error = "body required" });
            var (db, f) = await d.ContextAsync();
            var plan = await db.DrainPlans.FirstOrDefaultAsync(p => p.Id == id);
            var row = await db.MigrationJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.DrainPlanId == id);
            if (plan == null || row == null) return Results.NotFound();
            if (plan.Status != DrainPlanStatus.Draft) return Results.BadRequest(new { error = "only a draft plan can be edited" });
            if (b.Action != null) row.RowAction = b.Action.Value;
            if (b.Seq != null) row.Seq = b.Seq.Value;
            if (b.TargetHostId != null)
            {
                var t = f.ById(b.TargetHostId);
                if (t == null || t.Host.Id == row.SourceHostId) return Results.BadRequest(new { error = "unknown target" });
                row.TargetHostId = t.Host.Id; row.TargetNode = t.Name; row.TargetStorage = null; row.PlanNote = null;
                var (src, g) = Find(f, row.SourceHostId, row.GuestType, row.Vmid);
                if (src != null && g != null)
                {
                    var opt = FleetAnalysis.RankTargets(f, src, g).First(o => o.HostId == t.Host.Id);
                    row.TargetStorage = opt.SuggestedStorage; row.Mode = opt.Mode;
                    if (opt.Vetoed) row.PlanNote = "chosen by the operator despite: " + string.Join("; ", opt.Vetoes);
                }
            }
            if (b.Storage != null) row.TargetStorage = b.Storage;
            if (b.Mode != null) row.Mode = b.Mode.Value;
            await db.SaveChangesAsync();
            return Results.Json(await PlanDto(db, f, id), J);
        });

        app.MapPost("/api/fleet/drains/{id}/execute", async (string id, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("draining hosts");
            var b = await Body<DrainExecuteDto>(ctx);
            var db = d.Db();
            var plan = await db.DrainPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (plan == null) return Results.NotFound();
            if (plan.Status != DrainPlanStatus.Draft) return Results.BadRequest(new { error = $"plan is {plan.Status}" });
            if (b == null || !string.Equals(b.Confirm?.Trim(), $"drain {plan.SourceNode}", StringComparison.Ordinal))
                return Results.BadRequest(new { error = $"type exactly: drain {plan.SourceNode}" });
            var isReturn = plan.ReverseOfPlanId != null;
            if (!isReturn && (b.MaintenanceUntil == null || b.MaintenanceUntil <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(b.Reason)))
                return Results.BadRequest(new { error = "a maintenance reason and an expiry in the future are required" });
            var rows = await db.MigrationJobs.Where(j => j.DrainPlanId == id).ToListAsync();
            var moves = rows.Where(r => r.RowAction == DrainRowAction.Move).ToList();
            var unplaced = moves.Where(r => r.TargetHostId == null).Select(r => r.GuestName).ToList();
            if (unplaced.Count > 0) return Results.BadRequest(new { error = $"rows without a target: {string.Join(", ", unplaced)} — choose a target or set them to Stay" });

            var now = DateTimeOffset.UtcNow;
            var i = 0;
            foreach (var r in moves.OrderBy(r => r.Seq)) { r.Status = MigrationJobStatus.Queued; r.QueuedAt = now.AddMilliseconds(i++); r.Phase = "queued (drain)"; }
            foreach (var r in rows.Where(r => r.RowAction == DrainRowAction.Stay)) { r.Status = MigrationJobStatus.Skipped; r.Phase = "stays on the node"; }
            plan.Status = DrainPlanStatus.Running; plan.ApprovedAt = now; plan.ApprovedBy = User(ctx);
            if (isReturn)
            {
                var m = await db.NodeMaintenance.FirstOrDefaultAsync(x => x.HostId == plan.SourceHostId);
                if (m != null) db.NodeMaintenance.Remove(m);
            }
            else
            {
                plan.MaintenanceUntil = b.MaintenanceUntil; plan.MaintenanceReason = b.Reason!.Trim();
                var m = await db.NodeMaintenance.FirstOrDefaultAsync(x => x.HostId == plan.SourceHostId);
                if (m == null) db.NodeMaintenance.Add(m = new NodeMaintenanceEntity { HostId = plan.SourceHostId, Since = now });
                m.Until = b.MaintenanceUntil!.Value; m.Reason = $"drain {plan.Id}: {b.Reason!.Trim()}"; m.By = User(ctx); m.DrainPlanId = plan.Id;
            }
            if (plan.ActionId != null)
            {
                var action = await db.Actions.FirstOrDefaultAsync(a => a.Id == plan.ActionId);
                if (action != null)
                {
                    db.ActionNotes.Add(new ActionNoteEntity { ActionId = action.Id, At = now, By = User(ctx), Text = $"Drain of {plan.SourceNode} started: {moves.Count} guest(s) queued (plan {plan.Id})." });
                    if (action.Status == ActionStatus.Open) { action.Status = ActionStatus.InProgress; action.UpdatedAt = now; }
                }
            }
            await db.SaveChangesAsync();
            await MigrationRunner.FinishPlanIfDoneAsync(db, plan.Id, ctx.RequestAborted);   // a plan of only Stay rows completes at once
            await Audit(d, ctx, isReturn ? "fleet.drain.return-approved" : "fleet.drain.approved", plan.Id, new { node = plan.SourceNode, moves = moves.Count, b.MaintenanceUntil, b.Reason });
            d.Hub.Broadcast("fleetJob", new { drain = plan.Id });
            return Results.Ok(new { ok = true, queued = moves.Count });
        });

        app.MapPost("/api/fleet/drains/{id}/abort", async (string id, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("draining hosts");
            var b = await Body<AbortDto>(ctx) ?? new AbortDto();
            var db = d.Db();
            var plan = await db.DrainPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (plan == null) return Results.NotFound();
            if (plan.Status != DrainPlanStatus.Running) return Results.BadRequest(new { error = $"plan is {plan.Status}" });
            plan.AbortRequested = true;
            if (b.Now)
                foreach (var j in await db.MigrationJobs.Where(j => j.DrainPlanId == id && j.Status == MigrationJobStatus.Running).ToListAsync())
                    j.CancelRequested = true;
            await db.SaveChangesAsync();
            await Audit(d, ctx, "fleet.drain.abort", plan.Id, new { now = b.Now });
            return Results.Ok(new { ok = true, message = b.Now ? "Aborting — the running copy is being cancelled; moved guests stay where they are" : "Aborting after the current move — moved guests stay where they are" });
        });

        app.MapPost("/api/fleet/drains/{id}/discard", async (string id, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("draining hosts");
            var db = d.Db();
            var plan = await db.DrainPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (plan == null) return Results.NotFound();
            if (plan.Status != DrainPlanStatus.Draft) return Results.BadRequest(new { error = "only a draft can be discarded" });
            plan.Status = DrainPlanStatus.Discarded;
            db.MigrationJobs.RemoveRange(await db.MigrationJobs.Where(j => j.DrainPlanId == id).ToListAsync());
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // Return to service: a NEW draft that moves each drained guest back, from wherever it is now.
        // Always a fresh human decision — it is never executed automatically.
        app.MapPost("/api/fleet/drains/{id}/reverse", async (string id, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("draining hosts");
            var (db, f) = await d.ContextAsync();
            var plan = await db.DrainPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (plan?.ReportJson == null) return Results.BadRequest(new { error = "the plan has no completed moves to reverse" });
            var home = f.ById(plan.SourceHostId);
            if (home == null) return Results.BadRequest(new { error = "the drained node is no longer registered" });
            var rev = JsonDocument.Parse(plan.ReportJson).RootElement.GetProperty("reverse");
            var back = new DrainPlanEntity { SourceHostId = plan.SourceHostId, SourceNode = plan.SourceNode, ReverseOfPlanId = plan.Id, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = User(ctx), ActionId = plan.ActionId };
            var rows = new List<MigrationJobEntity>(); var seq = 0;
            foreach (var r in rev.EnumerateArray())
            {
                var name = r.GetProperty("guest").GetString() ?? "";
                // find the guest where it lives now — by name, since VMIDs may have changed
                var now = FleetAnalysis.AllGuests(f).FirstOrDefault(p => string.Equals(p.Guest.Name, name, StringComparison.OrdinalIgnoreCase) && p.Node.Host.Id != plan.SourceHostId);
                var row = new MigrationJobEntity
                {
                    DrainPlanId = back.Id, Seq = seq++, Status = MigrationJobStatus.Planned, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = User(ctx),
                    GuestName = name, GuestType = r.GetProperty("type").GetString() ?? "qemu",
                    TargetHostId = home.Host.Id, TargetNode = home.Name, TargetVmid = r.GetProperty("originalVmid").GetInt32(),
                };
                if (now.Guest == null) { row.RowAction = DrainRowAction.Stay; row.PlanNote = "not found anywhere in the fleet now"; row.SourceHostId = ""; row.SourceNode = "?"; }
                else
                {
                    row.SourceHostId = now.Node.Host.Id; row.SourceNode = now.Node.Name; row.Vmid = now.Guest.Vmid; row.WasRunning = now.Guest.Running;
                    var opt = FleetAnalysis.RankTargets(f, now.Node, now.Guest).FirstOrDefault(o => o.HostId == home.Host.Id);
                    row.TargetStorage = opt?.SuggestedStorage; row.Mode = opt?.Mode ?? MigrationMode.Offline;
                    row.BytesPlanned = now.Guest.Disks.All(x => x.AllocatedBytes != null) ? now.Guest.Disks.Sum(x => x.AllocatedBytes!.Value) : null;
                    if (opt?.Vetoed == true) row.PlanNote = "home node currently vetoed: " + string.Join("; ", opt.Vetoes);
                }
                rows.Add(row);
            }
            db.DrainPlans.Add(back); db.MigrationJobs.AddRange(rows);
            await db.SaveChangesAsync();
            return Results.Json(await PlanDto(db, f, back.Id), J);
        });

        // ── rules ────────────────────────────────────────────────────────────
        app.MapPost("/api/fleet/rules", async (Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("editing placement rules");
            var b = await Body<RuleDto>(ctx);
            if (b?.Kind == null || string.IsNullOrWhiteSpace(b.Name) || b.Members == null || b.Members.Count == 0)
                return Results.BadRequest(new { error = "kind, name and at least one member are required" });
            var db = d.Db();
            var r = new FleetRuleEntity { Kind = b.Kind.Value, Name = b.Name.Trim(), MembersJson = JsonSerializer.Serialize(Clean(b.Members)), Note = b.Note, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = User(ctx) };
            db.FleetRules.Add(r); await db.SaveChangesAsync();
            await Audit(d, ctx, "fleet.rule.create", r.Id.ToString(), b);
            return Results.Ok(new { ok = true, id = r.Id });
        });
        app.MapMethods("/api/fleet/rules/{id:long}", ["PATCH"], async (long id, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("editing placement rules");
            var b = await Body<RuleDto>(ctx);
            var db = d.Db();
            var r = await db.FleetRules.FirstOrDefaultAsync(x => x.Id == id);
            if (r == null || b == null) return Results.NotFound();
            if (b.Kind != null) r.Kind = b.Kind.Value;
            if (!string.IsNullOrWhiteSpace(b.Name)) r.Name = b.Name.Trim();
            if (b.Members != null) r.MembersJson = JsonSerializer.Serialize(Clean(b.Members));
            if (b.Note != null) r.Note = b.Note;
            await db.SaveChangesAsync();
            await Audit(d, ctx, "fleet.rule.update", id.ToString(), b);
            return Results.Ok(new { ok = true });
        });
        app.MapDelete("/api/fleet/rules/{id:long}", async (long id, Deps d, HttpContext ctx) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.DrainHost)) return Forbid("editing placement rules");
            var db = d.Db();
            var r = await db.FleetRules.FirstOrDefaultAsync(x => x.Id == id);
            if (r == null) return Results.NotFound();
            db.FleetRules.Remove(r); await db.SaveChangesAsync();
            await Audit(d, ctx, "fleet.rule.delete", id.ToString(), new { r.Name });
            return Results.Ok(new { ok = true });
        });
    }

    // ── drain planning ───────────────────────────────────────────────────────

    // Order: running guests that would not come back after a reboot go first (they are the fragile
    // ones); self-host guests (VMentory's own VM and what it needs) go last; stopped guests stay
    // unless the operator flips them. Each guest takes the best non-vetoed target with the load the
    // plan has already put on every target counted in.
    private static List<MigrationJobEntity> PlanRows(FleetCtx f, NodeCtx src, DrainPlanEntity plan, List<GuestReading> guests, string user)
    {
        int Rank(GuestReading g) =>
            FleetAnalysis.HasRule(f, g, FleetRuleKind.SelfHost) ? 3 : !g.Running ? 4 : g.Onboot == false ? 0 : 1;
        var ordered = guests.OrderBy(Rank).ThenByDescending(g => g.Disks.Sum(d => d.AllocatedBytes ?? 0)).ToList();

        var extra = new Dictionary<string, Load>();
        var diskUse = new Dictionary<(string Host, string Store), long>();
        var rows = new List<MigrationJobEntity>(); var seq = 0;
        foreach (var g in ordered)
        {
            var row = new MigrationJobEntity
            {
                DrainPlanId = plan.Id, Seq = seq++, Status = MigrationJobStatus.Planned, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = user,
                GuestType = g.Type, Vmid = g.Vmid, GuestName = g.Name, WasRunning = g.Running,
                SourceHostId = src.Host.Id, SourceNode = src.Name,
                BytesPlanned = g.Disks.All(d => d.AllocatedBytes != null) ? g.Disks.Sum(d => d.AllocatedBytes!.Value) : null,
            };
            var notes = new List<string>();
            if (!g.Running) { row.RowAction = DrainRowAction.Stay; notes.Add("stopped — stays unless you switch it to Move"); }
            if (g.MigrationBlockers.Count > 0) notes.Add("blocked: " + string.Join("; ", g.MigrationBlockers));
            if (FleetAnalysis.HasRule(f, g, FleetRuleKind.Ephemeral)) notes.Add("ephemeral — consider deleting it rather than moving it");
            if (FleetAnalysis.HasRule(f, g, FleetRuleKind.SelfHost)) notes.Add("self-host — moved last; only a live move is allowed (an offline move would stop VMentory before the copy)");
            if (g.Onboot == false && g.Running) notes.Add("onboot=0 — moved early");

            if (row.RowAction == DrainRowAction.Move)
            {
                var options = FleetAnalysis.RankTargets(f, src, g, extra);
                FleetAnalysis.TargetOption? pick = null; string? store = null;
                foreach (var o in options.Where(o => !o.Vetoed))
                {
                    var fit = o.Storages.Where(s => s.Fits && s.Avail != null && s.Total != null
                                                    && s.Avail.Value - (s.Need ?? 0) - diskUse.GetValueOrDefault((o.HostId, s.Storage)) >= s.Total.Value * FleetPolicy.PoolMinFreeFraction)
                                        .OrderByDescending(s => s.Avail!.Value - diskUse.GetValueOrDefault((o.HostId, s.Storage))).FirstOrDefault();
                    if (fit != null) { pick = o; store = fit.Storage; break; }
                }
                if (pick == null && options.Count == 0)
                    notes.Add("cannot be placed: no other Proxmox node is registered");
                else if (pick == null)
                    notes.Add("cannot be placed: " +string.Join(" | ", options.Select(o => $"{o.Node}: {(o.Vetoes.Count > 0 ? string.Join("; ", o.Vetoes) : "no store with room after the plan's other moves")}")));
                else
                {
                    row.TargetHostId = pick.HostId; row.TargetNode = pick.Node; row.TargetStorage = store; row.Mode = pick.Mode;
                    var add = new Load(FleetAnalysis.GuestMemImpact(g), g.Running ? g.Vcpus ?? 0 : 0);
                    extra[pick.HostId] = extra.TryGetValue(pick.HostId, out var e) ? e with { MemBytes = e.MemBytes + add.MemBytes, Vcpus = e.Vcpus + add.Vcpus } : add;
                    var need = pick.Storages.First(s => s.Storage == store).Need ?? 0;
                    diskUse[(pick.HostId, store!)] = diskUse.GetValueOrDefault((pick.HostId, store!)) + need;
                }
            }
            row.PlanNote = notes.Count > 0 ? string.Join(" · ", notes) : null;
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<object?> PlanDto(VMentoryDbContext db, FleetCtx f, string id)
    {
        var plan = await db.DrainPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null) return null;
        var rows = await db.MigrationJobs.AsNoTracking().Where(j => j.DrainPlanId == id).ToListAsync();
        var moves = rows.Where(r => r.RowAction == DrainRowAction.Move).ToList();
        var bytesKnown = moves.Sum(r => r.BytesPlanned ?? 0);
        var bytesUnknown = moves.Count(r => r.BytesPlanned == null);
        double? eta = f.RateBytesPerSec > 0 && bytesUnknown == 0 ? bytesKnown / f.RateBytesPerSec : null;
        return new
        {
            plan = new { plan.Id, plan.SourceHostId, plan.SourceNode, plan.Status, plan.ReverseOfPlanId, plan.ActionId, plan.MaintenanceUntil, plan.MaintenanceReason, plan.AbortRequested, plan.CreatedAt, plan.CreatedBy, plan.ApprovedAt, plan.ApprovedBy, plan.FinishedAt,
                     report = plan.ReportJson == null ? (JsonElement?)null : JsonDocument.Parse(plan.ReportJson).RootElement },
            confirmPhrase = $"drain {plan.SourceNode}",
            rows = rows.OrderBy(r => r.Seq).Select(r =>
            {
                var (src, g) = Find(f, r.SourceHostId, r.GuestType, r.Vmid);
                return new
                {
                    job = JobDto(r), r.RowAction, r.PlanNote,
                    targets = src != null && g != null && plan.Status == DrainPlanStatus.Draft
                        ? FleetAnalysis.RankTargets(f, src, g).Select(o => new { o.HostId, o.Node, o.Score, o.Vetoed, o.Vetoes, o.SuggestedStorage, o.Storages, mode = o.Mode.ToString() }).ToList<object>()
                        : null,
                    etaSeconds = r.BytesPlanned != null && f.RateBytesPerSec > 0 ? r.BytesPlanned / f.RateBytesPerSec : null,
                    downtime = r.RowAction == DrainRowAction.Stay ? "stays" : r.Mode == MigrationMode.Online ? "live" : r.WasRunning ? "down for the copy" : "already stopped",
                };
            }).ToList(),
            cost = new
            {
                moves = moves.Count, stays = rows.Count - moves.Count,
                unplaced = moves.Count(r => r.TargetHostId == null),
                bytesKnown, bytesUnknownGuests = bytesUnknown,
                etaSeconds = eta,
                etaBasis = f.RateBytesPerSec == null ? "no measured transfer rate yet" : $"{f.RateSamples} measured move(s)",
                downtimeGuests = moves.Count(r => r.WasRunning && r.Mode != MigrationMode.Online),
                liveGuests = moves.Count(r => r.Mode == MigrationMode.Online),
            },
        };
    }

    // ── DTO shaping ──────────────────────────────────────────────────────────

    private static object NodeDto(NodeCtx n, FleetCtx f, ScoreResult s)
    {
        var r = n.Reading;
        return new
        {
            hostId = n.Host.Id, node = n.Name, address = n.Host.Address, displayName = n.Host.DisplayName,
            reachable = n.Latest?.Ok, error = n.Latest?.Ok == false ? n.Latest.Error : null,
            takenAt = r?.TakenAt, ageSeconds = n.AgeSeconds, stale = n.Stale, warnings = n.Latest?.Warnings ?? [],
            pveVersion = r?.PveVersion, uptimeSeconds = r?.UptimeSeconds,
            hardware = new
            {
                status = n.Hardware?.Status.ToString(), serviceTag = n.ServiceTag, tagSource = n.ServiceTagSource,
                subsystems = n.Hardware?.Subsystems, faults = n.Hardware?.Faults, model = n.Hardware?.Model, managementIp = n.Hardware?.ManagementIp,
            },
            probe = new { ok = n.LatestProbe?.Ok, error = n.LatestProbe?.Error, takenAt = n.Probe?.TakenAt, product = n.Probe?.Product },
            maintenance = n.Maintenance == null ? null : new { n.Maintenance.Since, n.Maintenance.Until, n.Maintenance.Reason, n.Maintenance.By, active = n.InMaintenance(f.Now) },
            hasWriteToken = n.HasMigrateToken,
            capacity = FleetAnalysis.Capacity(n),
            score = s.Score, vetoed = s.Vetoes.Count > 0, vetoes = s.Vetoes, reasoning = s.Reasoning, parts = s.Parts, unknown = s.Unknown,
            guestsRunning = r == null ? (int?)null : FleetAnalysis.RunningGuests(r).Count(),
            guestsTotal = r?.Guests.Count(g => !g.Template),
            bootUnsafe = r == null ? (int?)null : FleetAnalysis.RunningGuests(r).Count(g => g.Onboot == false),
            cpuModel = r?.CpuModel,
        };
    }

    private static object GuestDto(NodeCtx n, GuestReading g, FleetCtx f) => new
    {
        hostId = n.Host.Id, node = n.Name, g.Vmid, g.Type, g.Name, g.Status, g.Lock, g.Vcpus, g.CpuUnlimited, g.MaxMem, g.Mem,
        g.Onboot, bootUnsafe = g.Running && g.Onboot == false, g.CpuType, cpuModel = n.Reading?.CpuModel, g.Tags, g.Disks, g.Bridges,
        g.HasIdeCloudInit, blockers = g.MigrationBlockers, g.ConfigError, g.UptimeSeconds,
        allocatedBytes = g.Disks.All(d => d.AllocatedBytes != null) ? g.Disks.Sum(d => d.AllocatedBytes!.Value) : (long?)null,
        virtualBytes = g.Disks.All(d => d.VirtualBytes != null) ? g.Disks.Sum(d => d.VirtualBytes!.Value) : (long?)null,
        rules = f.Rules.Where(r => FleetAnalysis.Members(r).Contains(g.Name, StringComparer.OrdinalIgnoreCase)).Select(r => new { r.Kind, r.Name }).ToList(),
        stale = n.Stale,
    };

    private static object JobDto(MigrationJobEntity j) => new
    {
        j.Id, j.DrainPlanId, j.Seq, j.GuestType, j.Vmid, j.GuestName, j.WasRunning, j.SourceHostId, j.SourceNode, j.TargetHostId, j.TargetNode,
        j.TargetVmid, j.TargetStorage, mode = j.Mode.ToString(), status = j.Status.ToString(), j.Phase, j.Error, j.BytesPlanned, j.Upid,
        j.PartialOnTarget, j.CleanupDone, j.SourceRestarted, j.CancelRequested, j.CreatedAt, j.CreatedBy, j.StartedAt, j.FinishedAt,
        j.TransferStartedAt, j.TransferEndedAt,
        measuredBytesPerSec = j.Status == MigrationJobStatus.Succeeded && j.BytesPlanned > 0 && j.TransferEndedAt > j.TransferStartedAt
            ? j.BytesPlanned!.Value / (j.TransferEndedAt!.Value - j.TransferStartedAt!.Value).TotalSeconds : (double?)null,
    };

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static (NodeCtx? Node, GuestReading? Guest) Find(FleetCtx f, string hostId, string type, int vmid)
    {
        var n = f.ById(hostId);
        return (n, n?.Reading?.Guests.FirstOrDefault(g => g.Vmid == vmid && g.Type == type));
    }

    private static List<string> Clean(List<string> m) => m.Select(x => x.Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static IResult Forbid(string what) => Results.Json(new { error = $"Your role does not allow {what}" }, statusCode: 403);
    private static string User(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.Name) ?? "?";
    private static async Task<T?> Body<T>(HttpContext ctx) where T : class
    {
        try { return await ctx.Request.ReadFromJsonAsync<T>(J); } catch { return null; }
    }

    private static async Task Audit(Deps d, HttpContext ctx, string verb, string? id, object detail)
    {
        try
        {
            using var s = d.Scopes.CreateScope();
            await s.ServiceProvider.GetRequiredService<IUserStore>().WriteAuditAsync(new AuditEventEntity
            {
                Timestamp = DateTimeOffset.UtcNow, Username = User(ctx), Verb = verb, Allowed = true,
                CorrelationId = id, Detail = JsonSerializer.Serialize(detail, J),
            });
        }
        catch { }
    }
}

// Request-scoped bundle so the handlers stay short.
public sealed class Deps(IServiceScopeFactory scopes, VMentoryDbContext db, FleetState state, FleetCollector collector, FleetOptions opt,
                         Store store, EstateState estate, EventHub hub)
{
    public IServiceScopeFactory Scopes => scopes;
    public FleetState State => state;
    public FleetCollector Collector => collector;
    public FleetOptions Opt => opt;
    public Store Store => store;
    public EstateState Estate => estate;
    public EventHub Hub => hub;
    public VMentoryDbContext Db() => db;

    public async Task<(VMentoryDbContext, FleetCtx)> ContextAsync() =>
        (db, FleetAnalysis.Build(state, store, estate, opt,
            await db.Machines.AsNoTracking().ToListAsync(), await db.FleetRules.AsNoTracking().ToListAsync(),
            await db.NodeMaintenance.AsNoTracking().ToListAsync(),
            await db.MigrationJobs.AsNoTracking().Where(j => j.Status == MigrationJobStatus.Succeeded).ToListAsync()));
}

public record MoveDto(string HostId, string Type, int Vmid, string? TargetHostId, string? Storage = null, MigrationMode? Mode = null,
                      bool? DryRun = true, string? Confirm = null, bool? AcknowledgeWarnings = null);
public record MaintenanceDto(bool On, string? Reason = null, DateTimeOffset? Until = null);
public record DrainCreateDto(string? ActionId = null);
public record RowDto(DrainRowAction? Action = null, string? TargetHostId = null, string? Storage = null, MigrationMode? Mode = null, int? Seq = null);
public record DrainExecuteDto(string? Confirm, DateTimeOffset? MaintenanceUntil = null, string? Reason = null);
public record AbortDto(bool Now = false);
public record RuleDto(FleetRuleKind? Kind = null, string? Name = null, List<string>? Members = null, string? Note = null);
