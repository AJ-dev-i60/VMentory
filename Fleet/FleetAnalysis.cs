using System.Text.Json;
using VMentory.Core;
using VMentory.Core.Estate;
using VMentory.Core.Fleet;
using VMentory.Core.Persistence;

namespace VMentory.Web.Fleet;

// ENG-0016 — everything the fleet view *computes*. Pure functions over the latest readings: no I/O,
// no stored numbers. Every figure here is either a field a node reported or arithmetic on such
// fields; where an input is missing the result is null and the reason travels with it.
public static class FleetPolicy
{
    // Policy, not data: the thresholds the brief set. They are shown in the UI as policy.
    public const long MemFloorMinBytes = 16L << 30;          // 16 GiB …
    public const double MemFloorFraction = 0.10;             // … or 10% of physical, whichever is greater
    public const double PoolMinFreeFraction = 0.10;          // a pool must keep 10% free after a move
    public const double WMem = 0.40, WCpu = 0.20, WSto = 0.20, WHw = 0.10, WLoad = 0.10;
    public const int RateSampleJobs = 10;

    public static long MemFloor(long physical) => Math.Max(MemFloorMinBytes, (long)(physical * MemFloorFraction));

    public static object Describe(FleetOptions opt) => new
    {
        memFloor = "16 GiB or 10% of physical memory, whichever is greater",
        poolMinFree = "a pool must keep 10% of its size free after the move",
        weights = new { memory = WMem, cpu = WCpu, storage = WSto, hardware = WHw, load = WLoad },
        staleAfterSeconds = StaleAfterSeconds(opt),
        liveMigration = "offered only between nodes whose CPU model string is identical; LXC never live (restart mode)",
        eta = $"bytes ÷ the transfer rate measured on this deployment's last {RateSampleJobs} successful moves; blank until one has run",
    };

    public static int StaleAfterSeconds(FleetOptions opt) => opt.IntervalSeconds * 3 + 30;
}

public sealed class NodeCtx
{
    public required Host Host { get; init; }
    public NodeReading? Reading { get; init; }      // last GOOD reading
    public NodeReading? Latest { get; init; }       // newest attempt (may have failed)
    public ProbeReading? Probe { get; init; }       // last good probe
    public ProbeReading? LatestProbe { get; init; }
    public bool Stale { get; init; }
    public double? AgeSeconds { get; init; }
    public HardwareDevice? Hardware { get; init; }
    public string? ServiceTag { get; init; }
    public string? ServiceTagSource { get; init; }
    public NodeMaintenanceEntity? Maintenance { get; init; }
    public bool HasMigrateToken { get; init; }

    public string Name => Reading?.Node ?? Latest?.Node ?? Host.DisplayName ?? Host.Address;
    public bool InMaintenance(DateTimeOffset now) => Maintenance != null && Maintenance.Until > now;
}

public sealed class FleetCtx
{
    public required List<NodeCtx> Nodes { get; init; }
    public required List<FleetRuleEntity> Rules { get; init; }
    public double? RateBytesPerSec { get; init; }
    public int RateSamples { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    public NodeCtx? ById(string hostId) => Nodes.FirstOrDefault(n => n.Host.Id == hostId);
}

public sealed record ScorePart(string Input, double Weight, double? Value, string Basis);
public sealed record StorageFit(string Storage, string Type, long? Avail, long? Total, long? Need, bool NeedIsVirtual, bool Fits, string Note);
public sealed record ScoreResult(int? Score, List<string> Vetoes, List<ScorePart> Parts, List<string> Unknown, string Reasoning, StorageFit? BestStorage, List<StorageFit> Storages);

// The extra load a candidate guest would put on a target, for guest-specific ranking and drain
// simulation. Null fields mean "this guest's figure is unknown".
public sealed record Load(long MemBytes, int Vcpus, GuestReading? Guest = null);

public static class FleetAnalysis
{
    // ── Building the context ─────────────────────────────────────────────────

    public static FleetCtx Build(FleetState state, Store store, EstateState estate, FleetOptions opt,
                                 List<MachineEntity> machines, List<FleetRuleEntity> rules,
                                 List<NodeMaintenanceEntity> maintenance, List<MigrationJobEntity> doneJobs)
    {
        var now = DateTimeOffset.UtcNow;
        var devices = (estate.Hardware?.Devices ?? []).Where(d => d.ServiceTag != "")
            .GroupBy(d => d.ServiceTag.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());

        var nodes = FleetCollector.FleetHosts(store).Select(h =>
        {
            var slot = state.For(h.Id);
            var good = slot.LastGood;
            var age = good == null ? (double?)null : (now - good.TakenAt).TotalSeconds;
            // service tag: what the node itself says (probe) first; the estate's machine record second
            string? tag = slot.LastGoodProbe?.Serial, tagSource = tag != null ? "node (DMI, via probe)" : null;
            if (tag == null)
            {
                var m = machines.FirstOrDefault(x => string.Equals(x.Address, h.Address, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(m?.ServiceTag)) { tag = m!.ServiceTag!.Trim(); tagSource = $"estate record '{m.Name}'"; }
            }
            HardwareDevice? dev = null;
            if (tag != null) devices.TryGetValue(tag.ToUpperInvariant(), out dev);
            return new NodeCtx
            {
                Host = h, Reading = good, Latest = slot.Latest, Probe = slot.LastGoodProbe, LatestProbe = slot.Probe,
                AgeSeconds = age, Stale = good == null || age > FleetPolicy.StaleAfterSeconds(opt) || slot.Latest?.Ok == false,
                Hardware = dev, ServiceTag = tag, ServiceTagSource = tagSource,
                Maintenance = maintenance.FirstOrDefault(x => x.HostId == h.Id),
                HasMigrateToken = opt.MigrateTokenFor(h.Address) != null,
            };
        }).OrderBy(n => n.Name).ToList();

        var samples = doneJobs.Where(j => j.Status == MigrationJobStatus.Succeeded && j.BytesPlanned > 0
                                          && j.TransferStartedAt != null && j.TransferEndedAt > j.TransferStartedAt)
                              .OrderByDescending(j => j.FinishedAt).Take(FleetPolicy.RateSampleJobs).ToList();
        double? rate = null;
        if (samples.Count > 0)
        {
            var secs = samples.Sum(j => (j.TransferEndedAt!.Value - j.TransferStartedAt!.Value).TotalSeconds);
            if (secs > 0) rate = samples.Sum(j => (double)j.BytesPlanned!.Value) / secs;
        }
        return new FleetCtx { Nodes = nodes, Rules = rules, RateBytesPerSec = rate, RateSamples = samples.Count, Now = now };
    }

    // ── Capacity ─────────────────────────────────────────────────────────────

    public static IEnumerable<GuestReading> RunningGuests(NodeReading r) => r.Guests.Where(g => g.Running && !g.Template);

    public static object Capacity(NodeCtx n)
    {
        var r = n.Reading;
        if (r == null) return new { known = false };
        var running = RunningGuests(r).ToList();
        var memUnknown = running.Count(g => g.MaxMem == null);
        long guestsAlloc = running.Sum(g => g.MaxMem ?? 0);
        var arcCap = n.Probe?.ArcMax;
        var cpuKnown = running.Where(g => g.Vcpus != null).ToList();
        var vcpu = cpuKnown.Sum(g => g.Vcpus!.Value);
        var unlimited = running.Count(g => g.CpuUnlimited);
        return new
        {
            known = true,
            memory = new
            {
                physical = r.MemTotal,
                guestsAllocated = guestsAlloc,
                guestsAllocatedUnknown = memUnknown,
                arcCap,
                arcCapSource = arcCap != null ? $"probe {n.Probe!.TakenAt:u}" : null,
                arcCapMissing = arcCap == null ? (n.LatestProbe?.Error ?? "probe not configured — ARC cap is not exposed by the REST API") : null,
                arcCurrent = r.ArcSize,
                allocated = arcCap != null ? guestsAlloc + arcCap : (long?)null,
                allocatedPct = arcCap != null && r.MemTotal > 0 ? Math.Round(100.0 * (guestsAlloc + arcCap.Value) / r.MemTotal.Value, 1) : (double?)null,
                guestsAllocatedPct = r.MemTotal > 0 ? Math.Round(100.0 * guestsAlloc / r.MemTotal.Value, 1) : (double?)null,
                available = r.MemAvailable,
                availablePct = r.MemAvailable != null && r.MemTotal > 0 ? Math.Round(100.0 * r.MemAvailable.Value / r.MemTotal.Value, 1) : (double?)null,
                used = r.MemUsed,
            },
            cpu = new
            {
                model = r.CpuModel, sockets = r.Sockets, cores = r.Cores, threads = r.Threads,
                vcpuCommitted = vcpu,
                unlimitedContainers = unlimited,
                ratio = r.Threads > 0 ? Math.Round((double)vcpu / r.Threads.Value, 2) : (double?)null,
                load1 = r.Load1, load5 = r.Load5, load15 = r.Load15,
                loadPerThread = r.Load15 != null && r.Threads > 0 ? Math.Round(r.Load15.Value / r.Threads.Value, 3) : (double?)null,
            },
            storage = r.Storages.Where(s => s.Enabled).Select(s => new
            {
                s.Id, s.Type, s.Content, s.Active, s.Shared, s.Total, s.Used, s.Avail, s.ZfsPool, s.Sparse,
                s.VirtualProvisioned, s.Volumes,
                usedPct = s.Total > 0 && s.Used != null ? Math.Round(100.0 * s.Used.Value / s.Total.Value, 1) : (double?)null,
                // brief §2: sum of guest virtual disk sizes against pool free space
                thinRatio = s.VirtualProvisioned != null && s.Avail > 0 ? Math.Round((double)s.VirtualProvisioned.Value / s.Avail.Value, 2) : (double?)null,
                holdsGuests = s.HoldsGuests,
            }).ToList(),
            zpools = r.Zpools.Select(z => new
            {
                z.Name, z.Size, z.Alloc, z.Free, z.Health, z.LayoutRead,
                layout = z.LayoutRead ? Layout(z) : null,
                vdevs = z.Vdevs, spares = z.Spares,
                sharedSpares = SharedSpares(r, z),
            }).ToList(),
        };
    }

    private static string Layout(ZpoolReading z)
    {
        var groups = z.Vdevs.GroupBy(v => System.Text.RegularExpressions.Regex.Replace(v.Name, @"-\d+$", "")).ToList();
        var parts = groups.Select(g => g.Key.StartsWith('/') ? $"{g.Count()} single disk{(g.Count() == 1 ? "" : "s")}"
                                                              : $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}");
        var spares = z.Spares.Count == 0 ? "no spare" : $"{z.Spares.Count} spare{(z.Spares.Count == 1 ? "" : "s")}";
        return string.Join(" + ", parts) + " · " + spares;
    }

    private static List<string> SharedSpares(NodeReading r, ZpoolReading z) =>
        r.Zpools.Where(o => o != z && o.Spares.Any(s => z.Spares.Any(t => t.Name == s.Name))).Select(o => o.Name).ToList();

    // ── Score ────────────────────────────────────────────────────────────────

    // Node-level veto reasons: these remove a node from every target list.
    public static List<string> NodeVetoes(NodeCtx n, FleetCtx f)
    {
        var v = new List<string>();
        if (n.InMaintenance(f.Now)) v.Add($"in maintenance until {n.Maintenance!.Until:yyyy-MM-dd HH:mm} UTC — {n.Maintenance.Reason}");
        if (n.Reading == null) v.Add("never read successfully" + (n.Latest?.Error is { } e ? $" — {e}" : ""));
        else if (n.Latest?.Ok == false) v.Add($"unreachable now — {n.Latest.Error}; last good read {Ago(n.AgeSeconds)}");
        else if (n.Stale) v.Add($"last read is stale ({Ago(n.AgeSeconds)})");
        if (n.Hardware?.Status == HardwareStatus.Critical) v.Add("hardware health is Critical");
        return v;
    }

    public static ScoreResult Score(NodeCtx n, FleetCtx f, Load? add = null, string? storageType = null, string? onlyStorage = null)
    {
        var vetoes = NodeVetoes(n, f);
        var parts = new List<ScorePart>();
        var unknown = new List<string>();
        var r = n.Reading;
        var storages = new List<StorageFit>();
        StorageFit? best = null;

        if (r != null)
        {
            // memory
            if (r.MemAvailable != null && r.MemTotal > 0)
            {
                var after = r.MemAvailable.Value - (add?.MemBytes ?? 0);
                parts.Add(new("memory", FleetPolicy.WMem, Clamp((double)after / r.MemTotal.Value),
                    $"{Gib(after)} available{(add != null ? " after the move" : "")} of {Gib(r.MemTotal)} physical"));
                if (add != null && after < FleetPolicy.MemFloor(r.MemTotal.Value))
                    vetoes.Add($"memory would fall to {Gib(after)}, below the floor of {Gib(FleetPolicy.MemFloor(r.MemTotal.Value))}");
            }
            else unknown.Add("memory (node did not report MemAvailable)");

            // cpu
            if (r.Threads > 0)
            {
                var committed = RunningGuests(r).Sum(g => g.Vcpus ?? 0) + (add?.Vcpus ?? 0);
                parts.Add(new("cpu", FleetPolicy.WCpu, Math.Max(0, 1 - (double)committed / r.Threads.Value),
                    $"{committed} vCPU committed on {r.Threads} threads"));
            }
            else unknown.Add("cpu (thread count not reported)");

            // storage — the best-fitting guest-capable store
            var content = storageType == "lxc" ? "rootdir" : "images";
            foreach (var s in r.Storages.Where(s => s.Active && s.Enabled && s.Content.Contains(content) && (onlyStorage == null || s.Id == onlyStorage)))
            {
                var fit = Fit(s, add?.Guest);
                storages.Add(fit);
            }
            best = storages.Where(s => s.Fits && s.Avail != null && s.Total > 0)
                           .OrderByDescending(s => (double)(s.Avail!.Value - (s.Need ?? 0)) / s.Total!.Value).FirstOrDefault();
            var forScore = best ?? storages.Where(s => s.Avail != null && s.Total > 0).OrderByDescending(s => (double)s.Avail!.Value / s.Total!.Value).FirstOrDefault();
            if (forScore != null)
                parts.Add(new("storage", FleetPolicy.WSto, Clamp((double)(forScore.Avail!.Value - (add != null ? forScore.Need ?? 0 : 0)) / forScore.Total!.Value),
                    $"{forScore.Storage}: {Gib(forScore.Avail)} free of {Gib(forScore.Total)}"));
            else unknown.Add("storage (no guest-capable store reported its size)");
            if (add?.Guest != null && best == null)
                vetoes.Add(storages.Count == 0 ? $"no active store accepts {content}" : "no store has room: " + string.Join("; ", storages.Select(s => $"{s.Storage} — {s.Note}")));

            // load
            if (r.Load15 != null && r.Threads > 0)
                parts.Add(new("load", FleetPolicy.WLoad, Math.Max(0, 1 - r.Load15.Value / r.Threads.Value), $"load15 {r.Load15:0.##} on {r.Threads} threads"));
            else unknown.Add("load (not reported)");
        }
        else unknown.Add("everything — no successful read of this node");

        // hardware
        var hw = n.Hardware?.Status;
        if (hw == HardwareStatus.Ok) parts.Add(new("hardware", FleetPolicy.WHw, 1.0, "OpenManage: OK"));
        else if (hw == HardwareStatus.Warning) parts.Add(new("hardware", FleetPolicy.WHw, 0.5, "OpenManage: Warning"));
        else if (hw == HardwareStatus.Critical) parts.Add(new("hardware", FleetPolicy.WHw, 0.0, "OpenManage: Critical"));
        else unknown.Add(n.ServiceTag == null ? "hardware (service tag unknown — no probe and no estate record)" : $"hardware (service tag {n.ServiceTag} not in the monitor's last reading)");

        // anti-affinity
        if (add?.Guest != null)
            foreach (var rule in f.Rules.Where(x => x.Kind == FleetRuleKind.AntiAffinity))
            {
                var members = Members(rule);
                if (!members.Contains(add.Guest.Name, StringComparer.OrdinalIgnoreCase)) continue;
                var clash = r?.Guests.Where(g => !g.Template && !string.Equals(g.Name, add.Guest.Name, StringComparison.OrdinalIgnoreCase)
                                                && members.Contains(g.Name, StringComparer.OrdinalIgnoreCase)).Select(g => g.Name).ToList() ?? [];
                if (clash.Count > 0) vetoes.Add($"anti-affinity '{rule.Name}': {string.Join(", ", clash)} already runs here");
            }

        var known = parts.Where(p => p.Value != null).ToList();
        int? score = known.Count == 0 ? null : (int)Math.Round(100 * known.Sum(p => p.Weight * p.Value!.Value) / known.Sum(p => p.Weight));

        var why = new List<string>();
        if (r?.MemAvailable != null) why.Add($"{Gib(r.MemAvailable)} free RAM");
        if (best != null) why.Add($"{Gib(best.Avail)} free on {best.Storage}");
        else if (storages.Count > 0) { var s = storages.OrderByDescending(x => x.Avail ?? 0).First(); why.Add($"{Gib(s.Avail)} free on {s.Storage}"); }
        if (r != null) why.Add($"{RunningGuests(r).Count()} guest{(RunningGuests(r).Count() == 1 ? "" : "s")} running");
        if (r?.Load15 != null) why.Add($"load {r.Load15:0.##}");
        if (unknown.Count > 0) why.Add($"scored without: {string.Join(", ", unknown.Select(u => u.Split(' ')[0]))}");

        return new ScoreResult(score, vetoes, parts, unknown, $"{n.Name} — {string.Join(", ", why)}", best, storages);
    }

    // How much a guest needs on a store: allocated on thin stores, the full virtual size on thick
    // ones (a non-sparse zvol reserves its whole size). Unknown allocated falls back to virtual —
    // conservative, and said so.
    public static StorageFit Fit(StorageReading s, GuestReading? g)
    {
        if (s.Avail == null || s.Total == null) return new(s.Id, s.Type, s.Avail, s.Total, null, false, false, "size not reported");
        if (g == null) return new(s.Id, s.Type, s.Avail, s.Total, null, false, true, "");
        bool? thin = s.Type switch { "lvmthin" => true, "zfspool" => s.Sparse, _ => null };
        long need = 0; var usedVirtual = false; var missing = 0;
        foreach (var d in g.Disks)
        {
            long? b = thin == true ? d.AllocatedBytes ?? d.VirtualBytes : d.VirtualBytes ?? d.AllocatedBytes;
            if (thin == true && d.AllocatedBytes == null) usedVirtual = true;
            if (thin != true) usedVirtual = true;
            if (b == null) { missing++; continue; }
            need += b.Value;
        }
        if (missing > 0) return new(s.Id, s.Type, s.Avail, s.Total, null, usedVirtual, false, $"{missing} disk size(s) unknown — cannot check space");
        var left = s.Avail.Value - need;
        var floor = (long)(s.Total.Value * FleetPolicy.PoolMinFreeFraction);
        var basis = thin == true ? (usedVirtual ? "allocated where known, else virtual" : "allocated") : thin == false ? "virtual (thick store)" : "virtual (store type not known to be thin)";
        return left >= floor
            ? new(s.Id, s.Type, s.Avail, s.Total, need, usedVirtual, true, $"needs {Gib(need)} ({basis}), leaves {Gib(left)}")
            : new(s.Id, s.Type, s.Avail, s.Total, need, usedVirtual, false, $"needs {Gib(need)} ({basis}), would leave {Gib(left)} — under the 10% floor of {Gib(floor)}");
    }

    public static long GuestMemImpact(GuestReading g) => g.Running || g.Onboot == true ? g.MaxMem ?? 0 : 0;

    // ── Targets for one guest ─────────────────────────────────────────────────

    public sealed record TargetOption(
        string HostId, string Node, string Address, int? Score, bool Vetoed, List<string> Vetoes, string Reasoning,
        List<ScorePart> Parts, List<string> Unknown, string? SuggestedStorage, List<StorageFit> Storages,
        MigrationMode Mode, string ModeReason, string? CpuModel);

    // extra: load already assigned to each target by an in-progress drain plan (simulation)
    public static List<TargetOption> RankTargets(FleetCtx f, NodeCtx source, GuestReading g, Dictionary<string, Load>? extra = null)
    {
        var load = new Load(GuestMemImpact(g), g.Running ? g.Vcpus ?? 0 : 0, g);
        return f.Nodes.Where(n => n.Host.Id != source.Host.Id).Select(n =>
        {
            var add = load;
            if (extra != null && extra.TryGetValue(n.Host.Id, out var e)) add = load with { MemBytes = load.MemBytes + e.MemBytes, Vcpus = load.Vcpus + e.Vcpus };
            var s = Score(n, f, add, g.Type);
            var vetoes = s.Vetoes.ToList();
            if (!n.HasMigrateToken) vetoes.Add("no write token configured for this node (VMENTORY_PVE_MIGRATE_TOKENS)");
            var missingBridges = g.Bridges.Where(b => n.Reading != null && !n.Reading.Bridges.Contains(b)).ToList();
            if (missingBridges.Count > 0) vetoes.Add($"bridge(s) {string.Join(", ", missingBridges)} not present on {n.Name}");
            var mode = DecideMode(f, source, n, g);
            return new TargetOption(n.Host.Id, n.Name, n.Host.Address, s.Score, vetoes.Count > 0, vetoes, s.Reasoning,
                s.Parts, s.Unknown, s.BestStorage?.Storage, s.Storages, mode.Mode, mode.Reason, n.Reading?.CpuModel);
        })
        .OrderBy(t => t.Vetoed ? 1 : 0).ThenByDescending(t => t.Score ?? -1).ToList();
    }

    public static (MigrationMode Mode, string Reason) DecideMode(FleetCtx f, NodeCtx source, NodeCtx target, GuestReading g)
    {
        if (g.Type == "lxc")
            return g.Running ? (MigrationMode.Restart, "containers cannot live-migrate: stopped, moved, started on the target")
                             : (MigrationMode.Offline, "container is stopped");
        if (!g.Running) return (MigrationMode.Offline, "guest is stopped");
        if (IsPinned(f, g)) return (MigrationMode.Offline, "pinned guest — offline moves only");
        var a = source.Reading?.CpuModel; var b = target.Reading?.CpuModel;
        if (a == null || b == null) return (MigrationMode.Offline, "CPU model of one node is unknown — live not offered");
        if (a != b) return (MigrationMode.Offline, $"different CPU models ({Short(a)} → {Short(b)}) — live migration would fail or crash the guest; it is shut down, moved and started");
        return (MigrationMode.Online, $"same CPU model on both ({Short(a)}) — live migration possible");
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    public static List<string> Members(FleetRuleEntity r)
    {
        try { return JsonSerializer.Deserialize<List<string>>(r.MembersJson) ?? []; } catch { return []; }
    }

    public static bool HasRule(FleetCtx f, GuestReading g, FleetRuleKind kind) =>
        f.Rules.Any(r => r.Kind == kind && Members(r).Contains(g.Name, StringComparer.OrdinalIgnoreCase));
    public static bool IsPinned(FleetCtx f, GuestReading g) => HasRule(f, g, FleetRuleKind.Pinned);

    public static IEnumerable<(NodeCtx Node, GuestReading Guest)> AllGuests(FleetCtx f) =>
        f.Nodes.Where(n => n.Reading != null).SelectMany(n => n.Reading!.Guests.Where(g => !g.Template).Select(g => (n, g)));

    // ── Findings ─────────────────────────────────────────────────────────────

    public static object Findings(FleetCtx f)
    {
        var bootSafety = f.Nodes.Where(n => n.Reading != null).Select(n => new
        {
            node = n.Name, hostId = n.Host.Id, stale = n.Stale,
            guests = RunningGuests(n.Reading!).Where(g => g.Onboot == false).Select(g => new { g.Vmid, g.Name, g.Type }).ToList(),
            running = RunningGuests(n.Reading!).Count(),
        }).Where(x => x.guests.Count > 0).ToList();

        var placed = AllGuests(f).ToList();
        var affinity = f.Rules.Where(r => r.Kind == FleetRuleKind.AntiAffinity).Select(r =>
        {
            var members = Members(r);
            var found = placed.Where(p => members.Contains(p.Guest.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var clashes = found.GroupBy(p => p.Node.Host.Id).Where(g => g.Count() > 1)
                               .Select(g => new { node = g.First().Node.Name, guests = g.Select(x => x.Guest.Name).ToList() }).ToList();
            var unseen = members.Where(m => !found.Any(p => string.Equals(p.Guest.Name, m, StringComparison.OrdinalIgnoreCase))).ToList();
            return new { rule = r.Name, ruleId = r.Id, members, violated = clashes.Count > 0, clashes, notInFleet = unseen,
                         placement = found.Select(p => new { guest = p.Guest.Name, node = p.Node.Name }).ToList() };
        }).ToList();

        var spares = f.Nodes.Where(n => n.Reading != null).SelectMany(n => n.Reading!.Zpools.Where(z => z.LayoutRead).Select(z => new
        {
            node = n.Name, pool = z.Name, spares = z.Spares.Count, sharedWith = SharedSpares(n.Reading!, z), health = z.Health,
        })).ToList();

        var ruleGuests = (FleetRuleKind k) => f.Rules.Where(r => r.Kind == k).SelectMany(r =>
            placed.Where(p => Members(r).Contains(p.Guest.Name, StringComparer.OrdinalIgnoreCase))
                  .Select(p => new { rule = r.Name, guest = p.Guest.Name, node = p.Node.Name, vmid = p.Guest.Vmid, status = p.Guest.Status })).ToList();

        var nodeProblems = f.Nodes.Select(n => new
        {
            node = n.Name, hostId = n.Host.Id,
            unreachable = n.Latest?.Ok == false ? n.Latest.Error : null,
            stale = n.Stale, age = n.AgeSeconds,
            warnings = n.Latest?.Warnings ?? [],
            probe = n.LatestProbe == null ? "not run" : n.LatestProbe.Ok ? null : n.LatestProbe.Error,
            configErrors = n.Reading?.Guests.Where(g => g.ConfigError != null).Select(g => $"{g.Name}: {g.ConfigError}").ToList() ?? [],
        }).Where(x => x.unreachable != null || x.stale || x.warnings.Count > 0 || x.probe != null || x.configErrors.Count > 0).ToList();

        return new
        {
            bootSafety, bootSafetyTotal = bootSafety.Sum(b => b.guests.Count),
            antiAffinity = affinity, antiAffinityViolations = affinity.Count(a => a.violated),
            spares, poolsWithoutSpare = spares.Count(s => s.spares == 0),
            pinned = ruleGuests(FleetRuleKind.Pinned), ephemeral = ruleGuests(FleetRuleKind.Ephemeral), selfHost = ruleGuests(FleetRuleKind.SelfHost),
            nodeProblems,
            cpuGroups = f.Nodes.Where(n => n.Reading?.CpuModel != null).GroupBy(n => n.Reading!.CpuModel!)
                           .Select(g => new { model = g.Key, nodes = g.Select(n => n.Name).ToList(), livePairs = g.Count() > 1 }).ToList(),
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    public static string Gib(long? b) => b == null ? "—" : b.Value >= 1L << 40 ? $"{b.Value / (double)(1L << 40):0.##} TiB" : $"{b.Value / (double)(1L << 30):0.#} GiB";
    public static string Ago(double? s) => s == null ? "never" : s < 120 ? $"{s:0}s ago" : s < 7200 ? $"{s / 60:0}m ago" : s < 172800 ? $"{s / 3600:0.#}h ago" : $"{s / 86400:0.#}d ago";
    public static string Short(string model) => System.Text.RegularExpressions.Regex.Replace(model.Replace("Intel(R) ", "").Replace("(R)", "").Replace(" CPU", ""), @"\s+@.*$", "").Trim();
    private static double Clamp(double v) => Math.Max(0, Math.Min(1, v));
}
