using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VMentory.Core.Estate;
using VMentory.Core.Fleet;
using VMentory.Core.Persistence;

namespace VMentory.Web.Fleet;

// Executes queued guest moves strictly one at a time, fleet-wide. The brief asks for "one migration
// at a time per node pair"; one at a time overall is stricter, and on a single 1 Gbps network it
// costs nothing — parallel copies only make every ETA wrong. Enforced here, server-side: the UI can
// queue as much as it likes, the runner still drains the queue serially.
public sealed class MigrationRunner(IServiceScopeFactory scopes, FleetState fleet, FleetCollector collector, FleetOptions opt,
                                    Store store, EstateState estate, EventHub hub, AppConfig config) : BackgroundService
{
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex Progress = new(@"transferred .+ of .+|estimated size is|^\d\d:\d\d:\d\d\s+[\d.]+[KMGT]?\s|\d+%", RegexOptions.Compiled);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.Persist) return;
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var id = await NextJobAsync(ct);
                if (id != null) await RunAsync(id, ct);
                else await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DevLog.Err($"[FLEET] runner: {ex.Message}"); await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        }
    }

    private async Task<string?> NextJobAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();

        // aborted drains: nothing further from them starts
        var aborting = await db.DrainPlans.Where(p => p.AbortRequested && p.Status == DrainPlanStatus.Running).ToListAsync(ct);
        foreach (var plan in aborting)
        {
            var queued = await db.MigrationJobs.Where(j => j.DrainPlanId == plan.Id && j.Status == MigrationJobStatus.Queued).ToListAsync(ct);
            foreach (var j in queued) { j.Status = MigrationJobStatus.Cancelled; j.Phase = "not started — drain aborted"; j.FinishedAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync(ct);
            await FinishPlanIfDoneAsync(db, plan.Id, ct);
        }

        // a job left Running by a restart is resumed by its UPID; one without a UPID never started a copy
        // (ordered in memory — SQLite cannot ORDER BY a DateTimeOffset, gotcha #8a)
        var running = (await db.MigrationJobs.Where(j => j.Status == MigrationJobStatus.Running).ToListAsync(ct)).OrderBy(j => j.StartedAt).FirstOrDefault();
        if (running != null) return running.Id;
        var next = await db.MigrationJobs.Where(j => j.Status == MigrationJobStatus.Queued).ToListAsync(ct);
        return next.OrderBy(j => j.QueuedAt).ThenBy(j => j.Seq).FirstOrDefault()?.Id;
    }

    private async Task RunAsync(string jobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VMentoryDbContext>();
        var job = await db.MigrationJobs.FirstAsync(j => j.Id == jobId, ct);

        async Task Save(string? phase = null)
        {
            if (phase != null) job.Phase = phase;
            await db.SaveChangesAsync(ct);
            hub.Broadcast("fleetJob", new { job.Id, job.Status, job.Phase, job.DrainPlanId });
        }
        async Task Fail(string error, bool afterCopyStarted)
        {
            job.Status = job.CancelRequested ? MigrationJobStatus.Cancelled : MigrationJobStatus.Failed;
            job.Error = error; job.FinishedAt = DateTimeOffset.UtcNow;
            if (afterCopyStarted) await AfterFailureAsync(job, ct);
            await Save();
            await Audit(scope, job, job.Status.ToString().ToLowerInvariant(), error);
            if (job.DrainPlanId != null) await FinishPlanIfDoneAsync(db, job.DrainPlanId, ct);
        }

        var src = store.GetHost(job.SourceHostId);
        var tgt = job.TargetHostId != null ? store.GetHost(job.TargetHostId) : null;
        if (src == null || tgt == null) { await Fail("source or target node is no longer registered", false); return; }
        var srcWrite = opt.MigrateTokenFor(src.Address);
        var tgtWrite = opt.MigrateTokenFor(tgt.Address);
        var srcRead = FleetCollector.ReadToken(store, src);
        var tgtRead = FleetCollector.ReadToken(store, tgt);
        if (srcWrite == null || tgtWrite == null || srcRead == null || tgtRead == null) { await Fail("a read or write token is missing for the source or target", false); return; }

        using var srcW = new PveClient(src.Address, srcWrite, src.SkipTlsVerification, TimeSpan.FromSeconds(60));
        using var srcR = new PveClient(src.Address, srcRead, src.SkipTlsVerification);
        using var tgtW = new PveClient(tgt.Address, tgtWrite, tgt.SkipTlsVerification, TimeSpan.FromSeconds(60));
        using var tgtR = new PveClient(tgt.Address, tgtRead, tgt.SkipTlsVerification);
        var sn = PveClient.Enc(job.SourceNode);

        // ── resume: the copy was already handed to Proxmox before a restart
        if (job.Status == MigrationJobStatus.Running && job.Upid != null)
        {
            _rate = (await BuildCtxAsync(db, ct)).RateFor(job.Mode).Rate;
            await TrackAsync(job, srcR, srcW, sn, Save, ct);
            await CompleteAsync(job, db, scope, srcR, tgtR, tgtW, Save, Fail, ct);
            return;
        }
        if (job.Status == MigrationJobStatus.Running)
        {
            // it never got as far as starting the copy; the guest may have been shut down by us
            await Fail("VMentory restarted before the copy was started; nothing was copied", afterCopyStarted: true);
            return;
        }

        job.Status = MigrationJobStatus.Running; job.StartedAt = DateTimeOffset.UtcNow;
        await Save("re-reading the fleet");
        await Audit(scope, job, "start", null);

        // ── fresh preflight at execution time: the plan may be hours old
        await collector.CollectOnceAsync(ct);
        var ctx = await BuildCtxAsync(db, ct);
        _rate = ctx.RateFor(job.Mode).Rate;
        var srcCtx = ctx.ById(src.Id); var tgtCtx = ctx.ById(tgt.Id);
        var guest = srcCtx?.Reading?.Guests.FirstOrDefault(g => g.Vmid == job.Vmid && g.Type == job.GuestType);
        if (srcCtx == null || tgtCtx == null || guest == null) { await Fail($"{job.GuestType} {job.Vmid} is no longer on {job.SourceNode}", false); return; }
        var actions = await ActionVmsAsync(db, ct);
        var pf = await FleetPreflight.RunAsync(ctx, srcCtx, guest, tgtCtx, job.TargetStorage, job.Mode, opt, store, actions, ct, job.TargetVmid);
        job.PreflightJson = JsonSerializer.Serialize(pf, J);
        if (!pf.CanExecute) { await Fail("preflight failed at execution time: " + string.Join("; ", pf.Blockers), false); return; }
        job.TargetVmid = pf.TargetVmid; job.TargetVmidWasFree = pf.TargetVmidWasFree; job.TargetStorage = pf.TargetStorage;
        job.BytesPlanned = pf.BytesAllocated; job.WasRunning = guest.Running; job.Mode = pf.Mode;
        _rate = ctx.RateFor(job.Mode).Rate;
        await Save("preflight passed");

        try
        {
            // ── offline move of a running VM: shut it down cleanly first. Never force.
            if (job.GuestType == "qemu" && job.WasRunning && job.Mode == MigrationMode.Offline)
            {
                // Proxmox uses the guest agent for this when one is configured and running, ACPI otherwise.
                await Save(guest.AgentEnabled ? "shutting the guest down (guest agent, 180 s)" : "shutting the guest down (ACPI power button, 180 s)");
                var up = await srcW.PostAsync($"/nodes/{sn}/qemu/{job.Vmid}/status/shutdown", [new("timeout", "180")], ct);
                await WaitTaskAsync(srcR, sn, up.GetString()!, TimeSpan.FromSeconds(240), ct);
                var cur = await srcR.GetAsync($"/nodes/{sn}/qemu/{job.Vmid}/status/current", ct);
                if (Pj.Str(cur, "status") != "stopped" && !guest.AgentEnabled)
                {
                    // Windows often ignores the first ACPI press while its console is asleep and honours the
                    // second (i60dc2, 2026-10-02: first press timed out, second shut it down in ~70 s).
                    await Save("clean shutdown timed out — pressing the ACPI power button once more (180 s)");
                    up = await srcW.PostAsync($"/nodes/{sn}/qemu/{job.Vmid}/status/shutdown", [new("timeout", "180")], ct);
                    await WaitTaskAsync(srcR, sn, up.GetString()!, TimeSpan.FromSeconds(240), ct);
                    cur = await srcR.GetAsync($"/nodes/{sn}/qemu/{job.Vmid}/status/current", ct);
                }
                if (Pj.Str(cur, "status") != "stopped")
                {
                    if (!job.ForceStopOnTimeout)
                    {
                        await Fail("the guest did not shut down cleanly (two attempts of 180 s without an agent, one with) and was NOT forced off (the operator did not allow it). " +
                                   (guest.AgentEnabled ? "Its guest agent did not complete the shutdown." : "It has no guest agent and ignored the ACPI power button — common for Windows guests; install the QEMU guest agent, or allow a forced power-off for this move.") +
                                   " The guest is still running on the source.", afterCopyStarted: false);
                        return;
                    }
                    await Save("clean shutdown timed out — forcing it off (operator allowed this)");
                    await Audit(scope, job, "forced-off", "clean shutdown timed out after 180 s");
                    var stop = await srcW.PostAsync($"/nodes/{sn}/qemu/{job.Vmid}/status/stop", null, ct);
                    await WaitTaskAsync(srcR, sn, stop.GetString()!, TimeSpan.FromSeconds(120), ct);
                    cur = await srcR.GetAsync($"/nodes/{sn}/qemu/{job.Vmid}/status/current", ct);
                    if (Pj.Str(cur, "status") != "stopped") { await Fail("the guest could not be stopped, even forced", afterCopyStarted: false); return; }
                }
            }
            if (job.CancelRequested) { await Fail("cancelled before the copy started", afterCopyStarted: true); return; }

            // ── target fingerprint, read live
            var certs = await tgtR.GetAsync($"/nodes/{PveClient.Enc(job.TargetNode!)}/certificates/info", ct);
            var fp = Pj.Arr(certs).Where(c => Pj.Str(c, "filename") is "pveproxy-ssl.pem" or "pve-ssl.pem")
                                  .OrderBy(c => Pj.Str(c, "filename") == "pveproxy-ssl.pem" ? 0 : 1).Select(c => Pj.Str(c, "fingerprint")).FirstOrDefault();
            if (fp == null) { await Fail("could not read the target's certificate fingerprint", afterCopyStarted: true); return; }

            var form = new List<KeyValuePair<string, string>>
            {
                new("target-vmid", job.TargetVmid!.Value.ToString()),
                new("target-endpoint", $"host={tgt.Address},apitoken=PVEAPIToken={tgtWrite},fingerprint={fp}"),
                new("target-storage", job.TargetStorage!),
                new("target-bridge", "1"),   // each bridge maps to the same-named bridge (preflight checked they exist)
            };
            if (job.Mode == MigrationMode.Online) form.Add(new("online", "1"));
            if (job.Mode == MigrationMode.Restart) form.Add(new("restart", "1"));
            if (job.RemoveSourceOnSuccess) form.Add(new("delete", "1"));
            // By default delete stays 0: Proxmox keeps the source copy stopped and locked (lock=migrate), so it
            // cannot be started by accident — the "never two runnable copies" guarantee — and it is the
            // rollback. Unlocking or removing it later needs root, so the operator can instead ask Proxmox to
            // delete it at the end of a successful move (delete=1, run by Proxmox's own worker).

            await Save("starting remote migration");
            var upid = await srcW.PostAsync($"/nodes/{sn}/{job.GuestType}/{job.Vmid}/remote_migrate", form, ct);
            job.Upid = upid.GetString();
            job.TransferStartedAt = DateTimeOffset.UtcNow;
            await Save("copying");
            await Audit(scope, job, "copy-started", job.Upid);

            await TrackAsync(job, srcR, srcW, sn, Save, ct);
            await CompleteAsync(job, db, scope, srcR, tgtR, tgtW, Save, Fail, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { await Fail(ex.Message, afterCopyStarted: true); }
    }

    // Follow the task until it stops; keep the log tail and the latest progress line.
    private async Task TrackAsync(MigrationJobEntity job, PveClient srcR, PveClient srcW, string sn, Func<string?, Task> save, CancellationToken ct)
    {
        var upid = PveClient.Enc(job.Upid!);
        var start = 0; var tail = new List<string>((job.LogTail ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var cancelSent = false;
        string? lastProgress = null;
        while (true)
        {
            var st = await srcR.GetAsync($"/nodes/{sn}/tasks/{upid}/status", ct);
            var log = await srcR.GetAsync($"/nodes/{sn}/tasks/{upid}/log?start={start}&limit=500", ct);
            foreach (var line in Pj.Arr(log))
            {
                start = Math.Max(start, (Pj.Int(line, "n") ?? start) );
                var t = Pj.Str(line, "t") ?? "";
                tail.Add(t);
                if (Progress.IsMatch(t)) lastProgress = Clean(t);
            }
            if (tail.Count > 40) tail = tail[^40..];
            job.Phase = ProgressPhase(job, lastProgress);
            job.LogTail = string.Join('\n', tail);

            if (job.CancelRequested && !cancelSent)
            {
                try { await srcW.DeleteAsync($"/nodes/{sn}/tasks/{upid}", ct); } catch { }
                cancelSent = true; job.Phase = "cancel sent to Proxmox";
            }
            await save(null);
            if (Pj.Str(st, "status") == "stopped")
            {
                job.Error = Pj.Str(st, "exitstatus") is "OK" ? null : Pj.Str(st, "exitstatus") ?? "task stopped without an exit status";
                job.TransferEndedAt = DateTimeOffset.UtcNow;
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            // pick up a cancel request written by the API between polls
            job.CancelRequested |= await CancelFlagAsync(job.Id, ct);
        }
    }

    // Proxmox logs no progress while it copies a stopped guest's disks (zfs send / pvesm export over the
    // tunnel), so the phase shows elapsed time and — when this deployment has measured moves — an estimate,
    // labelled as one. A real progress line (live drive-mirror "transferred X of Y") wins when there is one.
    private double? _rate;
    private string ProgressPhase(MigrationJobEntity job, string? line)
    {
        if (line != null && line.Contains("transferred")) return "copying — " + line;
        var el = job.TransferStartedAt is { } t0 ? (DateTimeOffset.UtcNow - t0).TotalSeconds : 0;
        var s = $"copying — {Dur(el)} elapsed";
        if (line != null) s += $" · {line}";
        if (_rate > 0 && job.BytesPlanned > 0)
        {
            var est = Math.Min(0.99, el * _rate.Value / job.BytesPlanned.Value);
            var left = Math.Max(0, job.BytesPlanned.Value / _rate.Value - el);
            s += $" · est. {est:P0}, ~{Dur(left)} left (estimated from the measured {_rate.Value / 1e6:0} MB/s — Proxmox reports no progress for this copy)";
        }
        return s;
    }

    private static string Dur(double s) => s < 90 ? $"{s:0}s" : s < 5400 ? $"{s / 60:0}m" : $"{s / 3600:0.0}h";

    // Proxmox task-log entries can carry several of zfs send's lines run together.
    private static string Clean(string t)
    {
        var m = Regex.Match(t, @"send of (\S+?)@__migration__ estimated size is ([\d.]+[KMGT]?)");
        if (m.Success) return $"sending {m.Groups[1].Value.Split('/').Last()} ({m.Groups[2].Value})";
        return t.Split('\r').Last().Trim();
    }

    private async Task<bool> CancelFlagAsync(string id, CancellationToken ct)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<VMentoryDbContext>();
        return await db.MigrationJobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.CancelRequested).FirstOrDefaultAsync(ct);
    }

    private async Task CompleteAsync(MigrationJobEntity job, VMentoryDbContext db, IServiceScope scope, PveClient srcR, PveClient tgtR, PveClient tgtW,
                                     Func<string?, Task> save, Func<string, bool, Task> fail, CancellationToken ct)
    {
        if (job.Error != null) { await fail($"Proxmox task ended: {job.Error}", true); return; }
        var tn = PveClient.Enc(job.TargetNode!);
        try
        {
            var cur = await tgtR.GetAsync($"/nodes/{tn}/{job.GuestType}/{job.TargetVmid}/status/current", ct);
            var notes = new List<string> { $"arrived on {job.TargetNode} as {job.GuestType} {job.TargetVmid}, status {Pj.Str(cur, "status")}" };
            if (job.GuestType == "qemu" && job.WasRunning && job.Mode == MigrationMode.Offline && Pj.Str(cur, "status") != "running")
            {
                await save("starting the guest on the target");
                var up = await tgtW.PostAsync($"/nodes/{tn}/qemu/{job.TargetVmid}/status/start", null, ct);
                await WaitTaskAsync(tgtR, tn, up.GetString()!, TimeSpan.FromSeconds(180), ct);
                cur = await tgtR.GetAsync($"/nodes/{tn}/qemu/{job.TargetVmid}/status/current", ct);
                notes.Add($"started on the target: {Pj.Str(cur, "status")}");
            }
            try
            {
                var srcCfg = await srcR.GetAsync($"/nodes/{PveClient.Enc(job.SourceNode)}/{job.GuestType}/{job.Vmid}/config", ct);
                var lk = Pj.Str(srcCfg, "lock");
                notes.Add(lk != null ? $"source copy kept on {job.SourceNode}, stopped, lock={lk} (cannot be started until unlocked)"
                                     : $"⚠ source copy on {job.SourceNode} is NOT locked — it could be started; remove or lock it");
            }
            catch (PveException ex) when ((int)ex.Status is 500 or 404) { notes.Add($"no source copy left on {job.SourceNode}"); }
            job.Status = MigrationJobStatus.Succeeded; job.FinishedAt = DateTimeOffset.UtcNow;
            job.Phase = string.Join("; ", notes);
            await save(null);
            await Audit(scope, job, "succeeded", job.Phase);
            if (job.DrainPlanId != null) await FinishPlanIfDoneAsync(db, job.DrainPlanId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await fail($"the task reported OK but the guest could not be verified on the target: {ex.Message}", true);
        }
    }

    // After a failure: record anything left on the target, and put the guest back in service on the
    // source if we were the ones who stopped it.
    private async Task AfterFailureAsync(MigrationJobEntity job, CancellationToken ct)
    {
        var src = store.GetHost(job.SourceHostId); var tgt = job.TargetHostId != null ? store.GetHost(job.TargetHostId) : null;
        try
        {
            if (tgt != null && job.TargetVmidWasFree && job.TargetVmid != null && FleetCollector.ReadToken(store, tgt) is { } tr)
            {
                using var t = new PveClient(tgt.Address, tr, tgt.SkipTlsVerification);
                try { await t.GetAsync($"/nodes/{PveClient.Enc(job.TargetNode!)}/{job.GuestType}/{job.TargetVmid}/status/current", ct); job.PartialOnTarget = true; }
                catch (PveException) { job.PartialOnTarget = false; }
            }
            if (src != null && job.WasRunning && opt.MigrateTokenFor(src.Address) is { } sw && FleetCollector.ReadToken(store, src) is { } sr)
            {
                using var r = new PveClient(src.Address, sr, src.SkipTlsVerification);
                using var w = new PveClient(src.Address, sw, src.SkipTlsVerification);
                var sn = PveClient.Enc(job.SourceNode);
                var cur = await r.GetAsync($"/nodes/{sn}/{job.GuestType}/{job.Vmid}/status/current", ct);
                var cfg = await r.GetAsync($"/nodes/{sn}/{job.GuestType}/{job.Vmid}/config", ct);
                if (Pj.Str(cur, "status") == "stopped" && Pj.Str(cfg, "lock") == null)
                {
                    await w.PostAsync($"/nodes/{sn}/{job.GuestType}/{job.Vmid}/status/start", null, ct);
                    job.SourceRestarted = true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { job.Error += $" (cleanup check failed: {ex.Message})"; }
        try { job.Error += await ResidueReportAsync(job, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { job.Error += $" (residue check failed: {ex.Message})"; }
    }

    // After an interrupted copy, re-probe both nodes and say exactly what was left behind and how to remove
    // it — the API cannot remove any of it without root (2026-10-02 campaign, T5).
    private async Task<string> ResidueReportAsync(MigrationJobEntity job, CancellationToken ct)
    {
        if (job.TransferStartedAt == null) return "";
        for (var i = 0; i < 5 && !await collector.CollectOnceAsync(ct, forceProbe: true); i++) await Task.Delay(3000, ct);
        var srcSlot = fleet.For(job.SourceHostId); var tgtSlot = job.TargetHostId != null ? fleet.For(job.TargetHostId) : null;
        var guest = srcSlot.LastGood?.Guests.FirstOrDefault(g => g.Vmid == job.Vmid && g.Type == job.GuestType);
        var vols = guest?.Disks.Select(d => d.Volume[(d.Volume.IndexOf(':') + 1)..]).ToList() ?? [];
        var left = new List<string>();
        foreach (var sn in srcSlot.LastGoodProbe?.MigrationSnapshots.Where(x => vols.Any(v => x.Contains("/" + v + "@"))) ?? [])
            left.Add($"snapshot {sn} on {job.SourceNode} — zfs destroy {sn}");
        if (tgtSlot?.LastGoodProbe is { } tp && job.TargetVmid is int tv && job.TargetVmidWasFree)
        {
            var cfg = tgtSlot.LastGood?.Guests.FirstOrDefault(g => g.Vmid == tv);
            if (cfg != null) left.Add($"placeholder {job.GuestType} {tv} on {job.TargetNode} (lock={cfg.Lock ?? "none"}, cannot start while locked) — qm unlock {tv}; qm destroy {tv} --purge");
            foreach (var k in tp.Allocated.Keys.Where(k => Regex.IsMatch(k, $@"^(zfs|lvm):.*/vm-{tv}-disk-\d+$")))
                left.Add($"volume {k[4..]} on {job.TargetNode} — {(k.StartsWith("zfs:") ? "zfs destroy" : "lvremove")} {k[4..]}");
        }
        return left.Count == 0 ? " · re-probed both nodes: nothing left behind" : " · LEFT BEHIND (remove as root): " + string.Join(" | ", left);
    }


    private static async Task WaitTaskAsync(PveClient r, string node, string upid, TimeSpan max, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + max;
        while (DateTimeOffset.UtcNow < until)
        {
            var st = await r.GetAsync($"/nodes/{node}/tasks/{PveClient.Enc(upid)}/status", ct);
            if (Pj.Str(st, "status") == "stopped") return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public static async Task FinishPlanIfDoneAsync(VMentoryDbContext db, string planId, CancellationToken ct)
    {
        var plan = await db.DrainPlans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan == null || plan.Status != DrainPlanStatus.Running) return;
        var jobs = await db.MigrationJobs.Where(j => j.DrainPlanId == planId && j.RowAction == DrainRowAction.Move).ToListAsync(ct);
        if (jobs.Any(j => j.Status is MigrationJobStatus.Queued or MigrationJobStatus.Running)) return;
        plan.FinishedAt = DateTimeOffset.UtcNow;
        plan.Status = plan.AbortRequested ? DrainPlanStatus.Aborted
                    : jobs.Any(j => j.Status == MigrationJobStatus.Failed) ? DrainPlanStatus.Failed : DrainPlanStatus.Completed;
        var ok = jobs.Where(j => j.Status == MigrationJobStatus.Succeeded).ToList();
        plan.ReportJson = JsonSerializer.Serialize(new
        {
            moved = ok.Count,
            failed = jobs.Count(j => j.Status == MigrationJobStatus.Failed),
            cancelled = jobs.Count(j => j.Status == MigrationJobStatus.Cancelled),
            bytes = ok.Sum(j => j.BytesPlanned ?? 0),
            bytesUnknown = ok.Count(j => j.BytesPlanned == null),
            seconds = ok.Where(j => j.StartedAt != null && j.FinishedAt != null).Sum(j => (j.FinishedAt!.Value - j.StartedAt!.Value).TotalSeconds),
            // the reverse plan: where each guest went, so "return to service" can put it back
            reverse = ok.Select(j => new { guest = j.GuestName, type = j.GuestType, fromHostId = j.TargetHostId, fromNode = j.TargetNode, vmid = j.TargetVmid, toHostId = j.SourceHostId, toNode = j.SourceNode, originalVmid = j.Vmid, storage = j.TargetStorage }).ToList(),
        }, J);
        await db.SaveChangesAsync(ct);
    }

    private async Task<FleetCtx> BuildCtxAsync(VMentoryDbContext db, CancellationToken ct) =>
        FleetAnalysis.Build(fleet, store, estate, opt,
            await db.Machines.AsNoTracking().ToListAsync(ct), await db.FleetRules.AsNoTracking().ToListAsync(ct),
            await db.NodeMaintenance.AsNoTracking().ToListAsync(ct),
            await db.MigrationJobs.AsNoTracking().Where(j => j.Status == MigrationJobStatus.Succeeded).ToListAsync(ct));

    public static async Task<List<(int Ref, string Title, List<string> Vms)>> ActionVmsAsync(VMentoryDbContext db, CancellationToken ct)
    {
        var rows = await db.Actions.AsNoTracking().Where(a => a.Status != ActionStatus.Done && a.Status != ActionStatus.Dismissed)
                                   .Select(a => new { a.Ref, a.Title, a.AffectedVmsJson }).ToListAsync(ct);
        return rows.Select(a =>
        {
            List<string> vms;
            try { vms = JsonSerializer.Deserialize<List<string>>(a.AffectedVmsJson) ?? []; } catch { vms = []; }
            return (a.Ref, a.Title, vms);
        }).ToList();
    }

    private static async Task Audit(IServiceScope scope, MigrationJobEntity job, string what, string? detail)
    {
        try
        {
            var us = scope.ServiceProvider.GetRequiredService<IUserStore>();
            await us.WriteAuditAsync(new AuditEventEntity
            {
                Timestamp = DateTimeOffset.UtcNow, Username = job.CreatedBy, Verb = $"fleet.migrate.{what}", Allowed = true,
                CorrelationId = job.Id,
                Detail = JsonSerializer.Serialize(new
                {
                    job.GuestName, job.GuestType, job.Vmid, source = job.SourceNode, target = job.TargetNode, job.TargetVmid,
                    mode = job.Mode.ToString(), bytes = job.BytesPlanned, job.DrainPlanId,
                    seconds = job.StartedAt != null && job.FinishedAt != null ? (job.FinishedAt.Value - job.StartedAt.Value).TotalSeconds : (double?)null,
                    outcome = job.Status.ToString(), detail,
                }, J),
            });
        }
        catch { /* audit must never break the runner */ }
    }
}
