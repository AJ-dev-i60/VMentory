using System.Text.Json;
using VMentory.Core.Fleet;
using VMentory.Core.Persistence;

namespace VMentory.Web.Fleet;

public sealed class PreflightResult
{
    public bool CanExecute => Blockers.Count == 0;
    public List<string> Blockers { get; set; } = [];       // the move cannot run until these are fixed
    public List<string> Warnings { get; set; } = [];       // the operator may override (vetoes land here when a vetoed target is chosen)
    public List<string> Info { get; set; } = [];

    public string Guest { get; set; } = "";
    public string GuestType { get; set; } = "";
    public int Vmid { get; set; }
    public string SourceHostId { get; set; } = "";
    public string SourceNode { get; set; } = "";
    public string TargetHostId { get; set; } = "";
    public string TargetNode { get; set; } = "";
    public int? TargetVmid { get; set; }
    public bool TargetVmidWasFree { get; set; }
    public string? TargetVmidNote { get; set; }
    public string? TargetStorage { get; set; }
    public List<object> StorageMap { get; set; } = [];
    public string BridgeMap { get; set; } = "";
    public MigrationMode Mode { get; set; }
    public string ModeReason { get; set; } = "";
    public bool WasRunning { get; set; }
    public long? BytesAllocated { get; set; }               // what a copy moves; null = at least one disk unknown
    public long BytesAllocatedKnown { get; set; }
    public int DisksUnknown { get; set; }
    public long? BytesVirtual { get; set; }
    public double? RateBytesPerSec { get; set; }
    public int RateSamples { get; set; }
    public double? EtaSeconds { get; set; }
    public string Downtime { get; set; } = "";
    public List<string> DependsOn { get; set; } = [];
    public List<object> BackupJobs { get; set; } = [];
    public int? Score { get; set; }
    public string? Reasoning { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string ConfirmPhrase => $"{Guest} to {TargetNode}";
}

public static class FleetPreflight
{
    public static async Task<PreflightResult> RunAsync(FleetCtx f, NodeCtx src, GuestReading g, NodeCtx tgt, string? storage,
        MigrationMode? requestedMode, FleetOptions opt, Store store, IEnumerable<(int Ref, string Title, List<string> Vms)> actions,
        CancellationToken ct, int? reserveVmid = null)
    {
        var p = new PreflightResult
        {
            Guest = g.Name, GuestType = g.Type, Vmid = g.Vmid, SourceHostId = src.Host.Id, SourceNode = src.Name,
            TargetHostId = tgt.Host.Id, TargetNode = tgt.Name, WasRunning = g.Running,

        };
        if (src.Host.Id == tgt.Host.Id) { p.Blockers.Add("source and target are the same node"); return p; }
        if (g.Template) p.Blockers.Add("templates are not moved by this tool");
        if (g.ConfigError != null) p.Blockers.Add($"the guest config could not be read: {g.ConfigError}");
        if (g.Lock != null) p.Blockers.Add($"the guest is locked ({g.Lock}) — a previous operation has not finished or was left behind");
        p.Blockers.AddRange(g.MigrationBlockers);
        if (!src.HasMigrateToken) p.Blockers.Add($"no write token configured for the source {src.Name}");
        if (!tgt.HasMigrateToken) p.Blockers.Add($"no write token configured for the target {tgt.Name}");
        if (src.Latest?.Ok == false) p.Blockers.Add($"the source {src.Name} is not answering: {src.Latest.Error}");
        if (tgt.Reading == null || tgt.Latest?.Ok == false) p.Blockers.Add($"the target {tgt.Name} is not answering");

        // score + vetoes (vetoes become warnings: any host is selectable, brief §4)
        var option = FleetAnalysis.RankTargets(f, src, g).FirstOrDefault(t => t.HostId == tgt.Host.Id);
        if (option != null)
        {
            p.Score = option.Score; p.Reasoning = option.Reasoning;
            foreach (var v in option.Vetoes.Where(v => !v.StartsWith("no write token") && !v.StartsWith("bridge")))
                p.Warnings.Add("veto: " + v);
        }

        // mode
        var (mode, why) = FleetAnalysis.DecideMode(f, src, tgt, g);
        if (requestedMode == MigrationMode.Online && mode != MigrationMode.Online)
            p.Blockers.Add($"live migration is not possible here: {why}");
        if (requestedMode == MigrationMode.Offline && mode == MigrationMode.Online) { mode = MigrationMode.Offline; why = "offline requested by the operator — the guest is shut down, moved and started"; }
        p.Mode = mode; p.ModeReason = why;

        // storage
        var content = g.Type == "lxc" ? "rootdir" : "images";
        var stores = tgt.Reading?.Storages.Where(s => s.Active && s.Enabled && s.Content.Contains(content)).ToList() ?? [];
        var chosen = storage != null ? stores.FirstOrDefault(s => s.Id == storage)
                                     : stores.Select(s => (s, fit: FleetAnalysis.Fit(s, g))).Where(x => x.fit.Fits)
                                             .OrderBy(x => FleetAnalysis.StoreRank(x.s.Type))
                                             .ThenByDescending(x => (double)(x.s.Avail!.Value - (x.fit.Need ?? 0)) / x.s.Total!.Value).Select(x => x.s).FirstOrDefault();
        if (chosen == null)
            p.Blockers.Add(storage != null ? $"store '{storage}' does not exist on {tgt.Name} or does not accept {content}"
                                           : $"no store on {tgt.Name} has room for this guest");
        else
        {
            p.TargetStorage = chosen.Id;
            var fit = FleetAnalysis.Fit(chosen, g);
            if (!fit.Fits) p.Warnings.Add($"space: {chosen.Id} — {fit.Note}");
            p.StorageMap = g.Disks.Select(d => (object)new { disk = d.Key, from = d.Storage, to = chosen.Id, virtualBytes = d.VirtualBytes, allocatedBytes = d.AllocatedBytes }).ToList();
            p.Info.Add($"storage: every disk lands on {chosen.Id} ({chosen.Type}) — {fit.Note}");
            // Live: the IDE cloud-init drive cannot be hot-removed (seen in this estate, Sept 2026). Offline: under test.
            if (g.HasIdeCloudInit && chosen.Type == "zfspool")
            {
                if (p.Mode == MigrationMode.Online)
                    p.Blockers.Add("an IDE cloud-init drive cannot be hot-removed during a live move — remove it (needs a guest restart) or move offline");
                else
                    p.Warnings.Add("the guest has an IDE cloud-init drive; Proxmox regenerates it on the target for an offline move");
            }
        }

        // bridges
        var missing = g.Bridges.Where(b => tgt.Reading != null && !tgt.Reading.Bridges.Contains(b)).ToList();
        if (missing.Count > 0) p.Blockers.Add($"bridge(s) {string.Join(", ", missing)} do not exist on {tgt.Name}");
        p.BridgeMap = g.Bridges.Count == 0 ? "no network interfaces" : string.Join(", ", g.Bridges.Select(b => $"{b} → {b}"));

        // bytes + ETA
        p.BytesAllocatedKnown = g.Disks.Sum(d => d.AllocatedBytes ?? 0);
        p.DisksUnknown = g.Disks.Count(d => d.AllocatedBytes == null);
        p.BytesAllocated = p.DisksUnknown == 0 ? p.BytesAllocatedKnown : null;
        p.BytesVirtual = g.Disks.All(d => d.VirtualBytes != null) ? g.Disks.Sum(d => d.VirtualBytes!.Value) : null;
        (p.RateBytesPerSec, p.RateSamples) = f.RateFor(p.Mode);
        if (p.BytesAllocated != null && p.RateBytesPerSec > 0) p.EtaSeconds = p.BytesAllocated.Value / p.RateBytesPerSec.Value;
        if (p.RateBytesPerSec == null) p.Info.Add($"ETA: no {(p.Mode == MigrationMode.Online ? "live" : "offline")} move has completed on this deployment yet, so there is no measured rate for this kind of move");
        p.Downtime = p.Mode switch
        {
            MigrationMode.Online => "live — a brief pause at switchover (not measured here)",
            _ when !g.Running => "none — the guest is already stopped",
            _ => p.EtaSeconds is { } eta ? $"about {Dur(eta)} of copy, plus shutdown and boot time (not measured)" : "the whole copy, plus shutdown and boot — duration unknown until a transfer rate has been measured",
        };

        // rules
        foreach (var r in f.Rules.Where(r => FleetAnalysis.Members(r).Contains(g.Name, StringComparer.OrdinalIgnoreCase)))
        {
            switch (r.Kind)
            {
                case FleetRuleKind.Pinned: p.Warnings.Add($"pinned ('{r.Name}'): must never run twice — the source copy stays locked by Proxmox after the move; do not unlock it while the moved copy exists"); break;
                case FleetRuleKind.Ephemeral: p.Warnings.Add($"ephemeral ('{r.Name}'): a deletion candidate rather than something to migrate"); break;
                // Hard block (2026-10-02, after VM 171 shut itself down mid-move): an offline/restart move of
                // the guest VMentory runs on makes VMentory shut down its own host before the copy starts, so
                // the move can never complete and the guest is left stopped. Only a live move keeps it running.
                case FleetRuleKind.SelfHost when g.Running && p.Mode != MigrationMode.Online:
                    p.Blockers.Add($"'{r.Name}': this guest runs VMentory (or something it needs). An {p.Mode.ToString().ToLowerInvariant()} move shuts it down before the copy starts — which stops VMentory, so the move can never finish and the guest is left stopped. Only a live move is allowed (same CPU model on both nodes); otherwise move it by hand with qm remote-migrate.");
                    break;
                case FleetRuleKind.SelfHost:
                    p.Warnings.Add($"'{r.Name}': this guest runs VMentory — expect this console to pause briefly at the live switchover");
                    break;
                case FleetRuleKind.AntiAffinity: p.DependsOn.Add($"anti-affinity group '{r.Name}': {string.Join(", ", FleetAnalysis.Members(r))}"); break;
            }
        }
        foreach (var a in actions.Where(a => a.Vms.Contains(g.Name, StringComparer.OrdinalIgnoreCase)))
            p.DependsOn.Add($"action #{a.Ref}: {a.Title}");
        p.Info.Add("dependencies: VMentory has no service-dependency map; listed are only the rules and actions that name this guest");
        p.Info.Add("backup windows: the Arcserve schedule feed is not connected — check Arcserve by hand for a job due in the window");

        // live checks against the nodes — the readings may be a minute old, these are not
        var srcTok = FleetCollector.ReadToken(store, src.Host);
        var tgtTok = FleetCollector.ReadToken(store, tgt.Host);
        if (tgtTok != null)
        {
            using var t = new PveClient(tgt.Host.Address, tgtTok, tgt.Host.SkipTlsVerification);
            var want = reserveVmid ?? g.Vmid;
            try
            {
                await t.GetAsync($"/cluster/nextid?vmid={want}", ct);
                p.TargetVmid = want; p.TargetVmidWasFree = true;
                p.TargetVmidNote = $"VMID {want} is free on {tgt.Name}";
            }
            catch (PveException ex) when ((int)ex.Status == 400)
            {
                var holder = tgt.Reading?.Guests.FirstOrDefault(x => x.Vmid == want);
                try
                {
                    var next = await t.GetAsync("/cluster/nextid", ct);
                    p.TargetVmid = int.TryParse(next.ValueKind == JsonValueKind.String ? next.GetString() : next.GetRawText(), out var nv) ? nv : null;
                    p.TargetVmidWasFree = p.TargetVmid != null;
                }
                catch (Exception e2) when (e2 is not OperationCanceledException) { p.Blockers.Add($"could not get a free VMID on {tgt.Name}: {e2.Message}"); }
                p.TargetVmidNote = $"VMID {want} is taken on {tgt.Name}" + (holder != null ? $" by '{holder.Name}' ({holder.Status}{(holder.Lock != null ? $", lock={holder.Lock}" : "")})" : "")
                                 + (p.TargetVmid != null ? $" — the guest will become VMID {p.TargetVmid}" : "");
                if (holder?.Lock == "migrate")
                    p.Warnings.Add($"VMID {want} on {tgt.Name} is a stopped copy left locked by an earlier move — that is the guest's old self; remove it by hand once you are sure, or accept the new VMID");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { p.Blockers.Add($"could not check VMID on {tgt.Name}: {ex.Message}"); }

            await BackupJobs(t, tgt.Name, g, p, ct);
        }
        await CheckWritePrivilegesAsync(src, tgt, g, p, opt, ct);
        CheckResidue(src, tgt, g, p);
        if (srcTok != null)
        {
            using var s = new PveClient(src.Host.Address, srcTok, src.Host.SkipTlsVerification);
            try
            {
                var snaps = await s.GetAsync($"/nodes/{PveClient.Enc(src.Name)}/{g.Type}/{g.Vmid}/snapshot", ct);
                var n = Pj.Arr(snaps).Count(x => Pj.Str(x, "name") != "current");
                if (n > 0) p.Warnings.Add($"the guest has {n} snapshot{(n == 1 ? "" : "s")} — Proxmox may refuse a remote migration with snapshots, and they do not travel with it");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { p.Warnings.Add($"snapshots not checked: {ex.Message}"); }
            await BackupJobs(s, src.Name, g, p, ct);
        }
        return p;
    }

    // Residue of an interrupted move (cancel, crash). Found 2026-10-02: a cancel mid-`zfs send` left
    // `vm-131-disk-1@__migration__` on the source, and the next move of that guest failed in seconds with
    // "dataset already exists"; the target kept a locked placeholder and an orphaned volume. The API cannot
    // remove either without root, so preflight names them and the exact commands.
    private static void CheckResidue(NodeCtx src, NodeCtx tgt, GuestReading g, PreflightResult p)
    {
        if (src.Probe == null) p.Info.Add("residue check skipped on the source: the node probe has not run");
        else
        {
            var mine = g.Disks.Select(d => d.Volume[(d.Volume.IndexOf(':') + 1)..]).ToList();
            var stale = src.Probe.MigrationSnapshots.Where(s => mine.Any(v => s.Contains("/" + v + "@"))).ToList();
            if (stale.Count > 0)
                p.Blockers.Add($"an earlier move of this guest was interrupted and left {stale.Count} snapshot(s) on {src.Name}; the next move would fail with \"dataset already exists\". Remove on {src.Name} as root: " +
                               string.Join("; ", stale.Select(s => $"zfs destroy {s}")) + $" (probe read {src.Probe.TakenAt:HH:mm} UTC)");
        }
        if (tgt.Probe != null && p.TargetVmid is int tv && p.TargetVmidWasFree)
        {
            var orphans = tgt.Probe.Allocated.Keys.Where(k => k.StartsWith("zfs:") && System.Text.RegularExpressions.Regex.IsMatch(k, $@"/(vm|base)-{tv}-disk-\d+$"))
                .Select(k => k[4..]).ToList();
            orphans.AddRange(tgt.Probe.Allocated.Keys.Where(k => k.StartsWith("lvm:") && System.Text.RegularExpressions.Regex.IsMatch(k, $@"/vm-{tv}-disk-\d+$")).Select(k => k[4..]));
            if (orphans.Count > 0)
                p.Blockers.Add($"VMID {tv} is free on {tgt.Name} but volume(s) named for it exist there — residue of an interrupted move: {string.Join(", ", orphans)}. Remove on {tgt.Name} as root (zfs destroy / lvremove) once you have checked they belong to no guest.");
        }
    }

    // The write tokens' actual privileges, read live from each node, against what this guest's move needs.
    // Found the hard way (2026-10-02): i60dc2 carries `startup:`, the target's final `config` step needs
    // Sys.Modify on / for that, and the move died after a full 6.5-minute copy.
    private static async Task CheckWritePrivilegesAsync(NodeCtx src, NodeCtx tgt, GuestReading g, PreflightResult p, FleetOptions opt, CancellationToken ct)
    {
        if (g.HasHookscript) p.Blockers.Add("the guest has a hookscript — only root@pam can set that on the target; remove it or move by hand");
        var src_ = new[] { "VM.Migrate" };
        var tgt_ = new List<string> { "VM.Allocate", "Sys.Incoming", "Datastore.AllocateSpace", "VM.Config.Disk", "VM.Config.Network", "VM.Config.Options", "VM.PowerMgmt" };
        if (g.HasStartup) tgt_.Add("Sys.Modify");
        foreach (var (node, need, label) in new[] { (src, (IEnumerable<string>)src_, "source"), (tgt, tgt_, "target") })
        {
            var tok = opt.MigrateTokenFor(node.Host.Address);
            if (tok == null) continue;   // reported elsewhere
            try
            {
                using var c = new PveClient(node.Host.Address, tok, node.Host.SkipTlsVerification);
                var perms = await c.GetAsync("/access/permissions?path=/", ct);
                var have = perms.TryGetProperty("/", out var root) ? root.EnumerateObject().Where(x => Pj.Str(root, x.Name) == "1").Select(x => x.Name).ToHashSet() : [];
                var missing = need.Where(n => !have.Contains(n)).ToList();
                if (missing.Count > 0)
                    p.Blockers.Add($"the write token on the {label} {node.Name} lacks {string.Join(", ", missing)}" +
                                   (missing.Contains("Sys.Modify") ? " — needed because the guest has a `startup:` (boot order) setting; re-run ops/provision-fleet-nodes.sh" : ""));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { p.Warnings.Add($"could not read the {label} write token's privileges: {ex.Message}"); }
        }
    }

    // Proxmox's own scheduled backup jobs (vzdump) that touch this guest, with their next run.
    private static async Task BackupJobs(PveClient pve, string node, GuestReading g, PreflightResult p, CancellationToken ct)
    {
        try
        {
            var jobs = await pve.GetAsync("/cluster/backup", ct);
            foreach (var j in Pj.Arr(jobs))
            {
                if (!Pj.Flag(j, "enabled") && Pj.Has(j, "enabled")) continue;
                var vmids = (Pj.Str(j, "vmid") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                var all = Pj.Flag(j, "all");
                var jobNode = Pj.Str(j, "node");
                if (!(vmids.Contains(g.Vmid.ToString()) || (all && (jobNode == null || jobNode == node)))) continue;
                var next = Pj.Long(j, "next-run");
                var nextAt = next != null ? DateTimeOffset.FromUnixTimeSeconds(next.Value) : (DateTimeOffset?)null;
                var inWindow = nextAt != null && p.EtaSeconds != null && nextAt <= DateTimeOffset.UtcNow.AddSeconds(p.EtaSeconds.Value);
                p.BackupJobs.Add(new { node, id = Pj.Str(j, "id"), schedule = Pj.Str(j, "schedule"), nextRun = nextAt, inWindow });
                if (inWindow) p.Warnings.Add($"a Proxmox backup job on {node} ({Pj.Str(j, "id")}) runs at {nextAt:HH:mm} UTC, inside the estimated window");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { p.Info.Add($"Proxmox backup jobs on {node} not checked: {ex.Message}"); }
    }

    public static string Dur(double s) => s < 90 ? $"{s:0}s" : s < 5400 ? $"{s / 60:0} min" : $"{s / 3600:0.#} h";
}
