using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VMentory.Core;
using VMentory.Core.Auth;
using VMentory.Core.Estate;
using VMentory.Core.Persistence;

namespace VMentory.Web;

// ENG-0015 — the estate dashboard and the remediation tracker.
// Reads need a session (the /api chokepoint). Writes need ConsolePermission.ManageActions.
public static class EstateEndpoints
{
    private static readonly JsonSerializerOptions J = EstateState.Json;

    public static void MapEstateEndpoints(this WebApplication app)
    {
        // ── Estate view ──────────────────────────────────────────────────────
        app.MapGet("/api/estate", async (Store store, EstateState state, IServiceScopeFactory scopes, OmeOptions ome) =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            var machines = await db.Machines.AsNoTracking().OrderBy(m => m.SortOrder).ThenBy(m => m.Name).ToListAsync();
            var open = await db.Actions.AsNoTracking()
                .Where(a => a.Status != ActionStatus.Done && a.Status != ActionStatus.Dismissed)
                .Select(a => new { a.Id, a.Ref, a.Priority, a.AffectedMachinesJson, a.ScheduledStart, a.ScheduledEnd, a.Title, a.Class })
                .ToListAsync();

            var hw = state.Hardware;
            var devByTag = (hw?.Devices ?? []).Where(d => d.ServiceTag != "").ToDictionary(d => d.ServiceTag.ToUpperInvariant(), d => d);
            var hosts = store.GetAllHosts();

            var views = machines.Select(m =>
            {
                devByTag.TryGetValue((m.ServiceTag ?? "").ToUpperInvariant(), out var dev);
                var host = MatchHost(hosts, m);
                var (vms, vmSource) = ResolveVms(m, host);
                var mine = open.Where(a => Lists(a.AffectedMachinesJson).Contains(m.Key)).ToList();
                return new
                {
                    key = m.Key, name = m.Name, address = m.Address, kind = m.Kind, role = m.Role, model = m.Model,
                    serviceTag = m.ServiceTag, managementIp = m.ManagementIp, notes = m.Notes,
                    hardware = dev == null ? null : new { dev.Status, dev.Connected, dev.Subsystems, dev.Faults, dev.DeviceName, dev.ManagementIp },
                    host = host == null ? null : new { host.Id, host.Platform, host.Reachability, host.ScanState, host.LastScanned, host.TotalCores, host.TotalRamGb, host.Fqdn },
                    vms, vmSource, staticVmsVerifiedAt = m.StaticVmsVerifiedAt, staticVmsSource = m.StaticVmsSource,
                    openActions = mine.Count,
                    worstOpenPriority = mine.Count == 0 ? (ActionPriority?)null : mine.Min(a => a.Priority),
                    openActionRefs = mine.OrderBy(a => a.Priority).Select(a => a.Ref).ToList(),
                };
            }).ToList();

            var summary = new
            {
                machines = views.Count,
                hardwareOk = views.Count(v => v.hardware?.Status == HardwareStatus.Ok),
                hardwareWarning = views.Count(v => v.hardware?.Status == HardwareStatus.Warning),
                hardwareCritical = views.Count(v => v.hardware?.Status == HardwareStatus.Critical),
                hardwareUnmonitored = views.Count(v => v.hardware == null),
                vms = views.Sum(v => v.vms.Count),
                vmsRunning = views.Sum(v => v.vms.Count(x => IsRunning(x.State))),
                vmsLive = views.Where(v => v.vmSource == "live").Sum(v => v.vms.Count),
                openActions = open.Count,
                openByPriority = open.GroupBy(a => a.Priority).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                scheduled = open.Where(a => a.ScheduledStart != null).OrderBy(a => a.ScheduledStart)
                                .Select(a => new { a.Id, a.Ref, a.Title, a.ScheduledStart, a.ScheduledEnd }).Take(5).ToList(),
                siteVisitItems = open.Count(a => a.Class != ActionClass.Remote),
            };

            return Results.Ok(new
            {
                facts = state.Facts,
                hardware = new
                {
                    configured = ome.Enabled, source = hw?.Source, takenAt = hw?.TakenAt, ok = hw?.Ok,
                    error = state.LastError, lastAttempt = state.LastAttempt, devices = hw?.Devices.Count ?? 0,
                },
                summary,
                machines = views,
            });
        });

        app.MapPost("/api/estate/refresh", async (HttpContext ctx, EstateCollector collector) =>
        {
            if (!Can(ctx)) return Forbid();
            var ran = await collector.CollectOnceAsync(ctx.RequestAborted);
            return Results.Ok(new { ok = ran, message = ran ? "Hardware monitor re-read" : "Monitor not configured or a read is already running" });
        });

        // ── Actions ──────────────────────────────────────────────────────────
        app.MapGet("/api/actions", async (Store store, IServiceScopeFactory scopes) =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            var items = await db.Actions.AsNoTracking()
                .Include(a => a.Notes).Include(a => a.BlockedBy).Include(a => a.Blocks)
                .ToListAsync();
            var machines = await db.Machines.AsNoTracking().ToListAsync();
            var statusById = items.ToDictionary(a => a.Id, a => a.Status);
            var hosts = store.GetAllHosts();
            return Results.Ok(items.OrderBy(a => a.Priority).ThenBy(a => a.Ref)
                .Select(a => ToDto(a, machines, hosts, statusById)).ToList());
        });

        app.MapPost("/api/actions", async (HttpContext ctx, Store store, EventHub hub, IServiceScopeFactory scopes) =>
        {
            if (!Can(ctx)) return Forbid();
            var body = await ReadBody<ActionWriteDto>(ctx);
            if (body == null || string.IsNullOrWhiteSpace(body.Title)) return Results.BadRequest(new { error = "title required" });

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            var now = DateTimeOffset.UtcNow;
            var e = new ActionItemEntity
            {
                Ref = (await db.Actions.MaxAsync(a => (int?)a.Ref) ?? 0) + 1,
                Title = body.Title.Trim(), Why = body.Why ?? "", How = body.How ?? "", DoneWhen = body.DoneWhen ?? "",
                Priority = body.Priority ?? ActionPriority.Medium, Class = body.Class ?? ActionClass.Remote,
                Group = Blank(body.Group), Status = ActionStatus.Open, Source = "manual",
                AffectedMachinesJson = JsonSerializer.Serialize(body.AffectedMachines ?? []),
                AffectedVmsJson = JsonSerializer.Serialize(body.AffectedVms ?? []),
                PurchasesJson = JsonSerializer.Serialize(body.Purchases ?? []),
                RequiresDowntime = body.RequiresDowntime ?? false,
                ScheduledStart = body.ScheduledStart, ScheduledEnd = body.ScheduledEnd,
                CreatedAt = now, UpdatedAt = now, CreatedBy = User(ctx),
            };
            db.Actions.Add(e);
            await db.SaveChangesAsync();

            // "Add a dependent item": the new item may be created already wired to what it waits
            // on (dependsOn) or to what waits on it (blocks). Cycles cannot arise on a fresh node.
            foreach (var id in body.DependsOn ?? [])
                if (await db.Actions.AnyAsync(a => a.Id == id)) db.ActionDependencies.Add(new ActionDependencyEntity { BlockedId = e.Id, BlockerId = id });
            foreach (var id in body.Blocks ?? [])
                if (await db.Actions.AnyAsync(a => a.Id == id)) db.ActionDependencies.Add(new ActionDependencyEntity { BlockedId = id, BlockerId = e.Id });
            await db.SaveChangesAsync();

            hub.Broadcast("actionsChanged", new { reason = "created", id = e.Id });
            return Results.Ok(await LoadDto(db, store, e.Id));
        });

        app.MapMethods("/api/actions/{id}", ["PATCH"], async (string id, HttpContext ctx, Store store, EventHub hub, IServiceScopeFactory scopes) =>
        {
            if (!Can(ctx)) return Forbid();
            var body = await ReadBody<ActionWriteDto>(ctx);
            if (body == null) return Results.BadRequest(new { error = "body required" });

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            var e = await db.Actions.FirstOrDefaultAsync(a => a.Id == id);
            if (e == null) return Results.NotFound();

            if (body.Title != null) e.Title = body.Title.Trim();
            if (body.Why != null) e.Why = body.Why;
            if (body.How != null) e.How = body.How;
            if (body.DoneWhen != null) e.DoneWhen = body.DoneWhen;
            if (body.Priority != null) e.Priority = body.Priority.Value;
            if (body.Class != null) e.Class = body.Class.Value;
            if (body.Group != null) e.Group = Blank(body.Group);
            if (body.AffectedMachines != null) e.AffectedMachinesJson = JsonSerializer.Serialize(body.AffectedMachines);
            if (body.AffectedVms != null) e.AffectedVmsJson = JsonSerializer.Serialize(body.AffectedVms);
            if (body.Purchases != null) e.PurchasesJson = JsonSerializer.Serialize(body.Purchases);
            if (body.RequiresDowntime != null) e.RequiresDowntime = body.RequiresDowntime.Value;
            if (body.ClearSchedule == true) { e.ScheduledStart = null; e.ScheduledEnd = null; }
            else
            {
                if (body.ScheduledStart != null) e.ScheduledStart = body.ScheduledStart;
                if (body.ScheduledEnd != null) e.ScheduledEnd = body.ScheduledEnd;
            }
            e.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            hub.Broadcast("actionsChanged", new { reason = "edited", id });
            return Results.Ok(await LoadDto(db, store, id));
        });

        app.MapPost("/api/actions/{id}/status", async (string id, HttpContext ctx, Store store, EventHub hub, IServiceScopeFactory scopes) =>
        {
            if (!Can(ctx)) return Forbid();
            var body = await ReadBody<StatusDto>(ctx);
            if (body == null || !Enum.TryParse<ActionStatus>(body.Status, true, out var status))
                return Results.BadRequest(new { error = "status must be Open, InProgress, Blocked, Done or Dismissed" });

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            var e = await db.Actions.Include(a => a.Notes).Include(a => a.BlockedBy).FirstOrDefaultAsync(a => a.Id == id);
            if (e == null) return Results.NotFound();

            var now = DateTimeOffset.UtcNow;
            var who = User(ctx);
            if (status == ActionStatus.Done)
            {
                // Ticking off something whose blockers are still open is allowed but recorded —
                // the operator may know better, and the note keeps that visible.
                var openBlockers = await db.Actions.Where(a => e.BlockedBy.Select(d => d.BlockerId).Contains(a.Id)
                                                              && a.Status != ActionStatus.Done && a.Status != ActionStatus.Dismissed)
                                                   .Select(a => a.Ref).ToListAsync();
                e.CompletedAt = now; e.CompletedBy = who;
                var note = $"Marked done by {who}";
                if (openBlockers.Count > 0) note += $" — with item{(openBlockers.Count == 1 ? "" : "s")} {string.Join(", ", openBlockers)} still open";
                if (!string.IsNullOrWhiteSpace(body.Note)) note += $": {body.Note.Trim()}";
                e.Notes.Add(new ActionNoteEntity { At = now, By = who, Text = note });
            }
            else
            {
                if (e.Status == ActionStatus.Done) { e.CompletedAt = null; e.CompletedBy = null; }
                var note = $"Status → {status} by {who}";
                if (!string.IsNullOrWhiteSpace(body.Note)) note += $": {body.Note.Trim()}";
                e.Notes.Add(new ActionNoteEntity { At = now, By = who, Text = note });
            }
            e.Status = status; e.UpdatedAt = now;
            await db.SaveChangesAsync();
            hub.Broadcast("actionsChanged", new { reason = "status", id, status = status.ToString() });
            return Results.Ok(await LoadDto(db, store, id));
        });

        app.MapPost("/api/actions/{id}/notes", async (string id, HttpContext ctx, Store store, EventHub hub, IServiceScopeFactory scopes) =>
        {
            if (!Can(ctx)) return Forbid();
            var body = await ReadBody<NoteDto>(ctx);
            if (body == null || string.IsNullOrWhiteSpace(body.Text)) return Results.BadRequest(new { error = "text required" });
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            var e = await db.Actions.FirstOrDefaultAsync(a => a.Id == id);
            if (e == null) return Results.NotFound();
            db.ActionNotes.Add(new ActionNoteEntity { ActionId = id, At = DateTimeOffset.UtcNow, By = User(ctx), Text = body.Text.Trim() });
            e.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            hub.Broadcast("actionsChanged", new { reason = "note", id });
            return Results.Ok(await LoadDto(db, store, id));
        });

        app.MapPost("/api/actions/{id}/deps", async (string id, HttpContext ctx, Store store, EventHub hub, IServiceScopeFactory scopes) =>
        {
            if (!Can(ctx)) return Forbid();
            var body = await ReadBody<DepDto>(ctx);
            if (body == null || string.IsNullOrWhiteSpace(body.BlockerId)) return Results.BadRequest(new { error = "blockerId required" });
            if (body.BlockerId == id) return Results.BadRequest(new { error = "an item cannot wait on itself" });

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            if (!await db.Actions.AnyAsync(a => a.Id == id) || !await db.Actions.AnyAsync(a => a.Id == body.BlockerId)) return Results.NotFound();
            if (await db.ActionDependencies.AnyAsync(d => d.BlockedId == id && d.BlockerId == body.BlockerId)) return Results.Ok(await LoadDto(db, store, id));

            // Reject a cycle: if the proposed blocker (transitively) waits on this item, adding the
            // edge makes both un-completable forever.
            var edges = await db.ActionDependencies.Select(d => new { d.BlockedId, d.BlockerId }).ToListAsync();
            var waitsOn = edges.GroupBy(d => d.BlockedId).ToDictionary(g => g.Key, g => g.Select(d => d.BlockerId).ToList());
            var seen = new HashSet<string>(); var stack = new Stack<string>([body.BlockerId]);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == id) return Results.BadRequest(new { error = "that would create a dependency cycle" });
                if (!seen.Add(cur)) continue;
                foreach (var next in waitsOn.GetValueOrDefault(cur) ?? []) stack.Push(next);
            }

            db.ActionDependencies.Add(new ActionDependencyEntity { BlockedId = id, BlockerId = body.BlockerId });
            await db.SaveChangesAsync();
            hub.Broadcast("actionsChanged", new { reason = "deps", id });
            return Results.Ok(await LoadDto(db, store, id));
        });

        app.MapDelete("/api/actions/{id}/deps/{blockerId}", async (string id, string blockerId, HttpContext ctx, Store store, EventHub hub, IServiceScopeFactory scopes) =>
        {
            if (!Can(ctx)) return Forbid();
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            var d = await db.ActionDependencies.FirstOrDefaultAsync(x => x.BlockedId == id && x.BlockerId == blockerId);
            if (d == null) return Results.NotFound();
            db.ActionDependencies.Remove(d);
            await db.SaveChangesAsync();
            hub.Broadcast("actionsChanged", new { reason = "deps", id });
            return Results.Ok(await LoadDto(db, store, id));
        });

        app.MapDelete("/api/actions/{id}", async (string id, HttpContext ctx, EventHub hub, IServiceScopeFactory scopes) =>
        {
            if (!RbacCatalog.Can(ctx.User, ConsolePermission.ManageUsers)) return Forbid();   // Admin only — deletion loses history; prefer Dismissed
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
            var e = await db.Actions.FirstOrDefaultAsync(a => a.Id == id);
            if (e == null) return Results.NotFound();
            db.Actions.Remove(e);
            await db.SaveChangesAsync();
            hub.Broadcast("actionsChanged", new { reason = "deleted", id });
            return Results.Ok(new { ok = true });
        });
    }

    // ── Assembly ─────────────────────────────────────────────────────────────

    private static async Task<object?> LoadDto(VMentoryDbContext db, Store store, string id)
    {
        var e = await db.Actions.AsNoTracking().Include(a => a.Notes).Include(a => a.BlockedBy).Include(a => a.Blocks).FirstOrDefaultAsync(a => a.Id == id);
        if (e == null) return null;
        var machines = await db.Machines.AsNoTracking().ToListAsync();
        var ids = e.BlockedBy.Select(d => d.BlockerId).Concat(e.Blocks.Select(d => d.BlockedId)).Distinct().ToList();
        var statuses = await db.Actions.AsNoTracking().Where(a => ids.Contains(a.Id) || a.Id == id).ToDictionaryAsync(a => a.Id, a => a.Status);
        return ToDto(e, machines, store.GetAllHosts(), statuses);
    }

    private static object ToDto(ActionItemEntity a, List<MachineEntity> machines, IReadOnlyList<Host> hosts, Dictionary<string, ActionStatus> statusById)
    {
        var affected = Lists(a.AffectedMachinesJson);
        var impact = ComputeImpact(affected, Lists(a.AffectedVmsJson), machines, hosts);
        var blockedBy = a.BlockedBy.Select(d => d.BlockerId).ToList();
        var openBlockers = blockedBy.Count(b => statusById.TryGetValue(b, out var s) && s != ActionStatus.Done && s != ActionStatus.Dismissed);
        return new
        {
            a.Id, a.Ref, a.Title, a.Why, a.How, a.DoneWhen, a.Priority, a.Class, a.Group, a.Status, a.Source, a.SourceKey,
            a.LastDetectedAt, affectedMachines = affected, affectedVms = Lists(a.AffectedVmsJson), purchases = Lists(a.PurchasesJson),
            a.RequiresDowntime, a.ScheduledStart, a.ScheduledEnd, a.CreatedAt, a.UpdatedAt, a.CompletedAt, a.CreatedBy, a.CompletedBy,
            dependsOn = blockedBy, blocks = a.Blocks.Select(d => d.BlockedId).ToList(), openBlockers,
            notes = a.Notes.OrderBy(n => n.At).Select(n => new { n.At, n.By, n.Text }).ToList(),
            impact,
        };
    }

    // Blast radius: for every affected machine, the guests that go dark — live inventory when the
    // machine is a scanned hypervisor host, the recorded static list otherwise, labelled as such.
    public static ActionImpact ComputeImpact(List<string> machineKeys, List<string> extraVms, List<MachineEntity> machines, IReadOnlyList<Host> hosts)
    {
        var result = new List<ImpactMachine>();
        foreach (var key in machineKeys)
        {
            var m = machines.FirstOrDefault(x => x.Key == key);
            if (m == null) { result.Add(new ImpactMachine(key, key, "?", [])); continue; }
            var host = MatchHost(hosts, m);
            var (vms, _) = ResolveVms(m, host);
            result.Add(new ImpactMachine(m.Key, m.Name, m.Kind.ToString(), vms));
        }
        if (extraVms.Count > 0)
            result.Add(new ImpactMachine("_named", "Named explicitly", "Vm", extraVms.Select(v => new ImpactVm(v, null, "?", "named")).ToList()));
        var all = result.SelectMany(r => r.Vms).ToList();
        return new ActionImpact(result, all.Count, all.Count(v => IsRunning(v.State)));
    }

    private static Host? MatchHost(IReadOnlyList<Host> hosts, MachineEntity m) =>
        hosts.FirstOrDefault(h => string.Equals(h.Address, m.Address, StringComparison.OrdinalIgnoreCase))
        ?? hosts.FirstOrDefault(h => !string.IsNullOrEmpty(h.Fqdn) && (string.Equals(h.Fqdn, m.Key, StringComparison.OrdinalIgnoreCase)
                                                                     || h.Fqdn.StartsWith(m.Key + ".", StringComparison.OrdinalIgnoreCase)));

    private static (List<ImpactVm> Vms, string Source) ResolveVms(MachineEntity m, Host? host)
    {
        if (host != null && host.Vms.Count > 0)
            return (host.Vms.Select(v => new ImpactVm(v.Name, null, v.State, "live")).ToList(), "live");
        List<StaticVm> stat;
        try { stat = JsonSerializer.Deserialize<List<StaticVm>>(m.StaticVmsJson, J) ?? []; } catch { stat = []; }
        return stat.Count > 0
            ? (stat.Select(v => new ImpactVm(v.Name, v.Address, v.State, "static")).ToList(), "static")
            : ([], "none");
    }

    private static bool IsRunning(string state) => state.Equals("running", StringComparison.OrdinalIgnoreCase) || state.Equals("Running", StringComparison.Ordinal);

    private static List<string> Lists(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; } catch { return []; }
    }

    private static bool Can(HttpContext ctx) => RbacCatalog.Can(ctx.User, ConsolePermission.ManageActions);
    private static IResult Forbid() => Results.Json(new { error = "Your role cannot change the action list" }, statusCode: 403);
    private static string User(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.Name) ?? "?";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static async Task<T?> ReadBody<T>(HttpContext ctx) where T : class
    {
        try { return await ctx.Request.ReadFromJsonAsync<T>(); } catch { return null; }
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────
public record ActionWriteDto(
    string? Title = null, string? Why = null, string? How = null, string? DoneWhen = null,
    ActionPriority? Priority = null, ActionClass? Class = null, string? Group = null,
    List<string>? AffectedMachines = null, List<string>? AffectedVms = null, List<string>? Purchases = null,
    bool? RequiresDowntime = null, DateTimeOffset? ScheduledStart = null, DateTimeOffset? ScheduledEnd = null, bool? ClearSchedule = null,
    List<string>? DependsOn = null, List<string>? Blocks = null);
public record StatusDto(string Status, string? Note = null);
public record NoteDto(string Text);
public record DepDto(string BlockerId);
