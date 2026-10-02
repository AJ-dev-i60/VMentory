using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using VMentory.Core;
using VMentory.Core.Fleet;

namespace VMentory.Web.Fleet;

// Latest fleet readings in memory. Per node: the newest attempt and the newest *good* reading, so a
// node that stops answering keeps showing what it last said — with that reading's age — instead of
// collapsing to zeros.
public sealed class FleetState
{
    public sealed class Slot
    {
        public NodeReading? Latest { get; set; }
        public NodeReading? LastGood { get; set; }
        public ProbeReading? Probe { get; set; }
        public ProbeReading? LastGoodProbe { get; set; }
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    public DateTimeOffset? LastCycle { get; set; }

    public Slot For(string hostId) => _slots.GetOrAdd(hostId, _ => new Slot());
    public IReadOnlyDictionary<string, Slot> All => _slots;
    public void Forget(string hostId) => _slots.TryRemove(hostId, out _);

    // guests whose agent did not answer: skip re-asking until the time given, keep the reason
    public ConcurrentDictionary<string, (DateTimeOffset Until, string Reason)> AgentBackoff { get; } = new();
}

public sealed class FleetCollector(FleetOptions opt, FleetState state, Store store, EventHub hub, AppConfig config) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastProbe = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (config.MockMode) return;
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await CollectOnceAsync(ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DevLog.Err($"[FLEET] cycle failed: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(opt.IntervalSeconds), ct);
        }
    }

    public static IEnumerable<Host> FleetHosts(Store store) =>
        store.GetAllHosts().Where(h => h.Platform == PlatformKind.Proxmox);

    public static string? ReadToken(Store store, Host h)
    {
        var pw = store.GetEffectiveCreds(h)?.GetPassword();
        return string.IsNullOrWhiteSpace(pw) ? null : pw;
    }

    public async Task<bool> CollectOnceAsync(CancellationToken ct, bool forceProbe = false)
    {
        if (!await _gate.WaitAsync(0, ct)) return false;
        try
        {
            var hosts = FleetHosts(store).ToList();
            foreach (var gone in state.All.Keys.Except(hosts.Select(h => h.Id)).ToList()) state.Forget(gone);

            var probeDue = opt.ProbeEnabled && (forceProbe || DateTimeOffset.UtcNow - _lastProbe > TimeSpan.FromSeconds(opt.ProbeIntervalSeconds));
            await Task.WhenAll(hosts.Select(async h =>
            {
                var slot = state.For(h.Id);
                if (probeDue)
                {
                    var p = await opt.ProbeAsync(h.Address, ct);
                    slot.Probe = p;
                    if (p.Ok) slot.LastGoodProbe = p;
                }
                var reading = await ReadNodeAsync(h, slot.LastGoodProbe, ct);
                slot.Latest = reading;
                if (reading.Ok) slot.LastGood = reading;
            }));
            if (probeDue) _lastProbe = DateTimeOffset.UtcNow;
            state.LastCycle = DateTimeOffset.UtcNow;
            hub.Broadcast("fleetUpdated", new { at = state.LastCycle, nodes = hosts.Count });
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<NodeReading> ReadNodeAsync(Host host, ProbeReading? probe, CancellationToken ct)
    {
        var r = new NodeReading { HostId = host.Id, Address = host.Address, TakenAt = DateTimeOffset.UtcNow };
        var token = ReadToken(store, host);
        if (token == null) { r.Error = "No read-only API token configured for this node"; return r; }

        using var pve = new PveClient(host.Address, token, host.SkipTlsVerification);
        try
        {
            var nodes = await pve.GetAsync("/nodes", ct);
            var nodeEl = Pj.Arr(nodes).FirstOrDefault(n => Pj.Str(n, "status") == "online");
            if (nodeEl.ValueKind == JsonValueKind.Undefined) nodeEl = Pj.Arr(nodes).FirstOrDefault();
            r.Node = nodeEl.ValueKind == JsonValueKind.Undefined ? null : Pj.Str(nodeEl, "node");
            if (r.Node == null) { r.Error = "Node list was empty"; return r; }
            var n = PveClient.Enc(r.Node);

            var status = await pve.GetAsync($"/nodes/{n}/status", ct);
            r.PveVersion = Pj.Str(status, "pveversion");
            if (status.TryGetProperty("cpuinfo", out var cpu))
            {
                r.CpuModel = Pj.Str(cpu, "model")?.Trim();
                r.Sockets = Pj.Int(cpu, "sockets");
                r.Cores = Pj.Int(cpu, "cores");      // PVE reports cores across all sockets
                r.Threads = Pj.Int(cpu, "cpus");
            }
            if (status.TryGetProperty("memory", out var mem))
            {
                r.MemTotal = Pj.Long(mem, "total");
                r.MemUsed = Pj.Long(mem, "used");
                r.MemAvailable = Pj.Long(mem, "available");   // PVE ≥ 8.x; absent on older nodes → null
            }
            if (status.TryGetProperty("loadavg", out var load) && load.ValueKind == JsonValueKind.Array)
            {
                var l = load.EnumerateArray().Select(x => double.TryParse(x.GetString() ?? x.GetRawText(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null).ToList();
                if (l.Count == 3) { r.Load1 = l[0]; r.Load5 = l[1]; r.Load15 = l[2]; }
            }
            r.UptimeSeconds = Pj.Long(status, "uptime");

            await Soft(r, "ARC size (rrddata)", async () =>
            {
                var rrd = await pve.GetAsync($"/nodes/{n}/rrddata?timeframe=hour&cf=AVERAGE", ct);
                var last = Pj.Arr(rrd).LastOrDefault(x => Pj.Has(x, "arcsize"));
                if (last.ValueKind != JsonValueKind.Undefined) r.ArcSize = Pj.Long(last, "arcsize");
            });

            await Soft(r, "bridges", async () =>
            {
                var net = await pve.GetAsync($"/nodes/{n}/network?type=any_bridge", ct);
                r.Bridges = Pj.Arr(net).Select(x => Pj.Str(x, "iface")).OfType<string>().OrderBy(x => x).ToList();
            });

            await Soft(r, "storage", async () => await ReadStorageAsync(pve, n, r, ct));
            await Soft(r, "ZFS pools", async () => await ReadZpoolsAsync(pve, n, r, ct));
            await ReadGuestsAsync(pve, n, r, ct);
            ApplyAllocations(r, probe);

            r.Ok = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { r.Error = "Timed out"; }
        catch (PveException ex) when ((int)ex.Status is 401) { r.Error = "API token rejected (401)"; }
        catch (PveException ex) when ((int)ex.Status is 403) { r.Error = $"API token lacks privilege (403) on {ex.Path}"; }
        catch (Exception ex) { r.Error = ex.Message; }
        return r;
    }

    private static async Task Soft(NodeReading r, string what, Func<Task> read)
    {
        try { await read(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { r.Warnings.Add($"{what}: {ex.Message}"); }
    }

    private static async Task ReadStorageAsync(PveClient pve, string n, NodeReading r, CancellationToken ct)
    {
        var list = await pve.GetAsync($"/nodes/{n}/storage", ct);
        foreach (var s in Pj.Arr(list))
        {
            var st = new StorageReading
            {
                Id = Pj.Str(s, "storage") ?? "",
                Type = Pj.Str(s, "type") ?? "",
                Content = Pj.Str(s, "content") ?? "",
                Active = Pj.Flag(s, "active"),
                Enabled = Pj.Flag(s, "enabled"),
                Shared = Pj.Flag(s, "shared"),
                Total = Pj.Long(s, "total"),
                Used = Pj.Long(s, "used"),
                Avail = Pj.Long(s, "avail"),
            };
            // pool / vgname / path / sparse come from the probe (ApplyProbe): /storage/{id} needs
            // Datastore.Allocate, which the audit role deliberately does not have.
            r.Storages.Add(st);
        }
    }

    private static async Task ReadZpoolsAsync(PveClient pve, string n, NodeReading r, CancellationToken ct)
    {
        var pools = await pve.GetAsync($"/nodes/{n}/disks/zfs", ct);
        foreach (var p in Pj.Arr(pools))
        {
            var z = new ZpoolReading
            {
                Name = Pj.Str(p, "name") ?? "",
                Size = Pj.Long(p, "size"),
                Alloc = Pj.Long(p, "alloc"),
                Free = Pj.Long(p, "free"),
                Health = Pj.Str(p, "health"),
            };
            try
            {
                var tree = await pve.GetAsync($"/nodes/{n}/disks/zfs/{PveClient.Enc(z.Name)}", ct);
                // root → children[0] is the pool vdev itself; its children are the data vdevs. "spares",
                // "logs" and "cache" appear as siblings of the pool vdev.
                foreach (var top in Pj.Arr(tree.TryGetProperty("children", out var c) ? c : default))
                {
                    var name = Pj.Str(top, "name") ?? "";
                    var kids = top.TryGetProperty("children", out var k) ? Pj.Arr(k).ToList() : [];
                    if (name == "spares")
                        z.Spares.AddRange(kids.Select(d => new VdevReading { Name = Pj.Str(d, "name") ?? "", State = Pj.Str(d, "state"), Disks = 1 }));
                    else if (name == z.Name)
                        z.Vdevs.AddRange(kids.Select(v => new VdevReading
                        {
                            Name = Pj.Str(v, "name") ?? "",
                            State = Pj.Str(v, "state"),
                            Disks = v.TryGetProperty("children", out var leaves) ? Math.Max(1, Pj.Arr(leaves).Count()) : 1,
                        }));
                }
                z.LayoutRead = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { r.Warnings.Add($"zpool {z.Name} layout: {ex.Message}"); }
            r.Zpools.Add(z);
        }
    }

    // Allocated bytes come from the probe (the audit token cannot read per-volume usage). Each disk
    // is matched to the probe line for its backing object; no line → stays null, i.e. "unknown".
    // Virtual provisioning per store is the sum of configured sizes from the guest configs.
    private static void ApplyAllocations(NodeReading r, ProbeReading? probe)
    {
        var byId = r.Storages.ToDictionary(s => s.Id);
        if (probe != null)
            foreach (var st in r.Storages)
            {
                if (!probe.StorageCfg.TryGetValue(st.Id, out var cfg)) continue;
                st.ZfsPath = cfg.GetValueOrDefault("pool");
                st.ZfsPool = st.ZfsPath?.Split('/')[0];
                st.VgName = cfg.GetValueOrDefault("vgname");
                st.Path = cfg.GetValueOrDefault("path");
                if (st.Type == "zfspool") st.Sparse = cfg.GetValueOrDefault("sparse") is "1" or "true";
            }
        if (probe != null)
            foreach (var st in r.Storages)
                if (probe.Media.TryGetValue(st.Id, out var disks) && disks.Count > 0)
                    (st.Media, st.MediaNote) = ClassifyMedia(disks);
        foreach (var d in r.Guests.SelectMany(g => g.Disks))
        {
            if (probe == null || !byId.TryGetValue(d.Storage, out var st)) continue;
            var name = d.Volume[(d.Volume.IndexOf(':') + 1)..];
            string? key = st.Type switch
            {
                "zfspool" when st.ZfsPath != null => $"zfs:{st.ZfsPath}/{name}",
                "lvmthin" or "lvm" when st.VgName != null => $"lvm:{st.VgName}/{name}",
                "dir" when st.Path != null => $"file:{st.Path.TrimEnd('/')}/images/{name}",
                _ => null,
            };
            if (key != null && probe.Allocated.TryGetValue(key, out var bytes)) { d.AllocatedBytes = bytes; d.AllocatedReadAt = probe.TakenAt; }
        }
        foreach (var st in r.Storages.Where(s => s.HoldsGuests))
        {
            var disks = r.Guests.SelectMany(g => g.Disks).Where(d => d.Storage == st.Id).ToList();
            st.Volumes = disks.Count;
            st.VirtualUnknown = disks.Count(d => d.VirtualBytes == null);
            st.VirtualProvisioned = disks.Sum(d => d.VirtualBytes ?? 0);
        }
    }

    // What the kernel says about the physical disks under a store. A RAID controller's virtual disk
    // (PERC, "Virtual Disk", LSI MR) reports the controller as its model and a meaningless rotational
    // flag — that is "unknown", never a guess.
    public static (string Media, string Note) ClassifyMedia(List<ProbeDisk> disks)
    {
        var models = string.Join(", ", disks.Select(d => d.Model).Where(m => m != "").Distinct());
        bool Virtual(ProbeDisk d) => System.Text.RegularExpressions.Regex.IsMatch(d.Model, @"PERC|Virtual Disk|LSI|MR9|RAID|Logical", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (disks.Any(Virtual))
            return ("unknown", $"behind a RAID controller ({models}) — the controller hides the media type");
        if (disks.Any(d => d.Rotational == null)) return ("unknown", $"rotational flag not reported ({models})");
        if (disks.All(d => d.Transport == "nvme")) return ("nvme", models);
        if (disks.All(d => d.Rotational == false)) return ("ssd", models);
        if (disks.All(d => d.Rotational == true)) return ("hdd", models);
        return ("mixed", models);
    }

    private async Task ReadGuestDetailAsync(PveClient pve, string n, GuestReading g, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (g.Type == "lxc")
            {
                var ifs = await pve.GetAsync($"/nodes/{n}/lxc/{g.Vmid}/interfaces", cts.Token);
                g.Ips = Pj.Arr(ifs).Where(i => Pj.Str(i, "name") != "lo")
                    .SelectMany(i => i.TryGetProperty("ip-addresses", out var a) ? Pj.Arr(a).Select(x => Pj.Str(x, "ip-address")) : [])
                    .OfType<string>().Where(UsefulIp).Distinct().ToList();
                g.IpSource = "container";
                return;
            }
            if (!g.AgentEnabled) { g.IpError = "no QEMU guest agent configured"; return; }
            var bk = $"{n}|{g.Vmid}";
            if (state.AgentBackoff.TryGetValue(bk, out var off) && off.Until > DateTimeOffset.UtcNow)
            { g.IpError = $"{off.Reason} (re-checked every 10 min)"; return; }
            var net = await pve.GetAsync($"/nodes/{n}/qemu/{g.Vmid}/agent/network-get-interfaces", cts.Token);
            var res = net.ValueKind == JsonValueKind.Object && net.TryGetProperty("result", out var rr) ? rr : default;
            g.Ips = Pj.Arr(res).Where(i => !(Pj.Str(i, "name") ?? "").StartsWith("lo"))
                .SelectMany(i => i.TryGetProperty("ip-addresses", out var a) ? Pj.Arr(a).Select(x => Pj.Str(x, "ip-address")) : [])
                .OfType<string>().Where(UsefulIp).Distinct().ToList();
            g.IpSource = "guest agent";
            state.AgentBackoff.TryRemove(bk, out _);
            var os = await pve.GetAsync($"/nodes/{n}/qemu/{g.Vmid}/agent/get-osinfo", cts.Token);
            if (os.ValueKind == JsonValueKind.Object && os.TryGetProperty("result", out var o)) g.OsName = Pj.Str(o, "pretty-name") ?? Pj.Str(o, "name");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { g.IpError = "guest agent did not answer in 8 s"; Backoff(n, g); }
        catch (PveException ex) { g.IpError = ex.Message.Contains("not running") ? "guest agent is not running in the guest" : ex.Message; Backoff(n, g); }
    }

    private void Backoff(string n, GuestReading g)
    {
        if (g.Type == "qemu" && g.IpError != null)
            state.AgentBackoff[$"{n}|{g.Vmid}"] = (DateTimeOffset.UtcNow.AddMinutes(10), g.IpError);
    }

    // loopback and link-local addresses say nothing about where a guest is reachable
    private static bool UsefulIp(string ip)
    {
        var a = ip.Split('%')[0];
        return a != "127.0.0.1" && a != "::1" && !a.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("169.254.");
    }

    private async Task ReadGuestsAsync(PveClient pve, string n, NodeReading r, CancellationToken ct)
    {
        foreach (var type in new[] { "qemu", "lxc" })
        {
            var list = await pve.GetAsync($"/nodes/{n}/{type}", ct);
            foreach (var g in Pj.Arr(list))
            {
                var guest = new GuestReading
                {
                    Vmid = Pj.Int(g, "vmid") ?? 0,
                    Type = type,
                    Name = Pj.Str(g, "name") ?? "",
                    Status = Pj.Str(g, "status") ?? "",
                    Lock = Pj.Str(g, "lock"),
                    Template = Pj.Flag(g, "template"),
                    MaxMem = Pj.Long(g, "maxmem"),
                    Mem = Pj.Long(g, "mem"),
                    UptimeSeconds = Pj.Long(g, "uptime"),
                    CpuBusy = Pj.Dbl(g, "cpu"),
                    Vcpus = type == "qemu" ? Pj.Int(g, "cpus") : null,
                };
                if (guest.Name == "") guest.Name = $"{type}-{guest.Vmid}";
                try
                {
                    var cfg = await pve.GetAsync($"/nodes/{n}/{type}/{guest.Vmid}/config", ct);
                    ApplyConfig(guest, cfg);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { guest.ConfigError = ex.Message; }
                r.Guests.Add(guest);
            }
        }
        // live addresses + OS name for running guests; a few at a time so one hung agent does not stall the node
        using var sem = new SemaphoreSlim(6);
        await Task.WhenAll(r.Guests.Where(g => g.Running && !g.Template).Select(async g =>
        {
            await sem.WaitAsync(ct);
            try { await ReadGuestDetailAsync(pve, n, g, ct); } finally { sem.Release(); }
        }));
    }

    private static readonly Regex QemuDiskKey = new(@"^(scsi|sata|ide|virtio|efidisk|tpmstate|unused)\d+$", RegexOptions.Compiled);
    private static readonly Regex LxcDiskKey = new(@"^(rootfs|mp\d+|unused\d+)$", RegexOptions.Compiled);

    public static void ApplyConfig(GuestReading g, JsonElement cfg)
    {
        g.Onboot = Pj.Flag(cfg, "onboot");
        g.OsType = Pj.Str(cfg, "ostype");
        g.HasStartup = Pj.Has(cfg, "startup");
        g.HasHookscript = Pj.Has(cfg, "hookscript");
        g.Description = Pj.Str(cfg, "description");
        g.AgentEnabled = (Pj.Str(cfg, "agent") ?? "").Split(',')[0] is "1" or "enabled=1";
        g.Tags = (Pj.Str(cfg, "tags") ?? "").Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (g.Type == "qemu")
        {
            g.CpuType = Pj.Str(cfg, "cpu") is { } c ? CpuTypeOf(c) : null;
        }
        else
        {
            var cores = Pj.Int(cfg, "cores");
            g.Vcpus = cores;
            g.CpuUnlimited = cores == null;
        }

        foreach (var p in cfg.EnumerateObject())
        {
            var key = p.Name;
            var val = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();

            if (key.StartsWith("net") && int.TryParse(key[3..], out _))
            {
                var br = Opt(val, "bridge");
                if (br != null && !g.Bridges.Contains(br)) g.Bridges.Add(br);
                continue;
            }
            if (key.StartsWith("hostpci") || (key.StartsWith("usb") && int.TryParse(key[3..], out _)))
            {
                g.MigrationBlockers.Add($"{key}: host device passthrough cannot move to another node");
                continue;
            }

            var isDisk = g.Type == "qemu" ? QemuDiskKey.IsMatch(key) : LxcDiskKey.IsMatch(key);
            if (!isDisk) continue;
            var first = val.Split(',')[0];
            if (first == "none" || first == "") continue;

            if (g.Type == "lxc" && first.StartsWith('/'))
            {
                g.MigrationBlockers.Add($"{key}: bind mount of host path {first} — not portable");
                continue;
            }
            if (g.Type == "qemu" && val.Contains("media=cdrom"))
            {
                if (first.Contains("cloudinit"))
                {
                    if (key.StartsWith("ide")) g.HasIdeCloudInit = true;
                }
                else if (first.Contains(":iso/"))
                {
                    g.MigrationBlockers.Add($"{key}: ISO {first} is mounted — eject it before moving");
                    g.IsoMounts.Add(key);
                    continue;
                }
                else continue;   // physical/empty cdrom
            }
            var colon = first.IndexOf(':');
            if (colon <= 0) continue;
            g.Disks.Add(new GuestDisk
            {
                Key = key,
                Storage = first[..colon],
                Volume = first,
                VirtualBytes = Pj.ParseSize(Opt(val, "size")),
                CloudInit = first.Contains("cloudinit"),
            });
        }
    }

    public static string CpuTypeOf(string cpu)
    {
        var first = cpu.Split(',')[0];
        return first.StartsWith("cputype=") ? first["cputype=".Length..] : first;
    }

    private static string? Opt(string val, string name)
    {
        foreach (var part in val.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == name) return part[(eq + 1)..];
        }
        return null;
    }
}
