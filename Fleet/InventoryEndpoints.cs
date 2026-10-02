using System.Security.Claims;
using System.Text.Json;
using VMentory.Core;
using VMentory.Core.Fleet;
using VMentory.Core.Persistence;

namespace VMentory.Web.Fleet;

// The guest browser (UI v4, "Proxmox Inventory — Guest Browser"). One tree: fleet → node → guest,
// every number read live or computed from live reads (ENG-0016 rule). Shares are what the design
// draws: this guest's slice of its node / pool (black) against everyone else's (grey).
public static class InventoryEndpoints
{
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Service icons can only come from what someone has stated: a Proxmox tag. Nothing probes ports
    // yet (see docs/ui-v4-gaps.md), so an untagged guest shows no service icon rather than a guess.
    private static readonly (string Icon, string Label, string[] Tags)[] ServiceTags =
    [
        ("www", "Web", ["www", "web", "http", "https", "nginx", "traefik", "iis"]),
        ("dkr", "Docker host", ["docker", "coolify", "portainer", "k3s", "k8s"]),
        ("sql", "Database", ["sql", "mssql", "mysql", "mariadb", "postgres", "postgresql", "pg", "db", "database"]),
        ("@", "Mail", ["mail", "smtp", "exchange", "imap"]),
        ("ad", "AD / DNS", ["ad", "dc", "dns", "domain-controller"]),
    ];

    public static void MapInventoryEndpoints(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AppConfig>().Persist) return;

        app.MapGet("/api/inventory", async (Deps d) =>
        {
            var (_, f) = await d.ContextAsync();
            var nodes = f.Nodes.Select(n => NodeDto(n, f)).ToList();
            var guests = FleetAnalysis.AllGuests(f).ToList();
            return Results.Json(new
            {
                lastCycle = d.State.LastCycle,
                intervalSeconds = d.Opt.IntervalSeconds,
                probeEnabled = d.Opt.ProbeEnabled,
                fleet = new
                {
                    nodes = f.Nodes.Count,
                    nodesReachable = f.Nodes.Count(n => n.Latest?.Ok == true),
                    guests = guests.Count,
                    running = guests.Count(p => p.Guest.Running),
                    vms = guests.Count(p => p.Guest.Type == "qemu"),
                    cts = guests.Count(p => p.Guest.Type == "lxc"),
                    threads = Sum(f.Nodes.Select(n => (long?)n.Reading?.Threads)),
                    memTotal = Sum(f.Nodes.Select(n => n.Reading?.MemTotal)),
                    memAvailable = Sum(f.Nodes.Select(n => n.Reading?.MemAvailable)),
                    vcpuCommitted = guests.Where(p => p.Guest.Running).Sum(p => p.Guest.Vcpus ?? 0),
                    memAllocated = guests.Where(p => p.Guest.Running).Sum(p => p.Guest.MaxMem ?? 0),
                },
                nodes,
                guests = guests.Select(p => GuestDto(p.Node, p.Guest, f)).ToList(),
            }, J);
        });

        // Start / shutdown / stop / reboot — the write token of the guest's node, state-checked here and
        // again by Proxmox, audited either way. Shutdown/stop/reboot need confirm:true (one-line confirm).
        app.MapPost("/api/inventory/guests/{hostId}/{type}/{vmid:int}/power", async (string hostId, string type, int vmid, Deps d, ActivityRegistry reg, HttpContext ctx) =>
        {
            PowerDto? b;
            try { b = await ctx.Request.ReadFromJsonAsync<PowerDto>(J); } catch { b = null; }
            var action = b?.Action?.ToLowerInvariant();
            var cap = action switch
            {
                "start" => ProviderCapability.Start,
                "shutdown" or "stop" => ProviderCapability.Stop,
                "reboot" => ProviderCapability.Reset,
                _ => (ProviderCapability?)null,
            };
            if (cap == null) return Results.BadRequest(new { error = "action must be start, shutdown, stop or reboot" });
            if (!RbacCatalog.Can(ctx.User, cap.Value)) return Results.Json(new { error = $"Your role cannot {action} guests" }, statusCode: 403);

            var (_, f) = await d.ContextAsync();
            var n = f.ById(hostId);
            var g = n?.Reading?.Guests.FirstOrDefault(x => x.Vmid == vmid && x.Type == type);
            if (n == null || g == null) return Results.NotFound(new { error = "guest not in the latest reading" });
            if (n.Latest?.Ok == false) return Results.Json(new { error = $"{n.Name} is not answering" }, statusCode: 409);
            if (g.Template) return Results.BadRequest(new { error = "templates cannot be started" });
            if (g.Lock != null) return Results.Json(new { error = $"the guest is locked ({g.Lock})" }, statusCode: 409);
            if (action == "start" && g.Running) return Results.Json(new { error = "already running" }, statusCode: 409);
            if (action != "start" && !g.Running) return Results.Json(new { error = $"cannot {action} — the guest is {g.Status}" }, statusCode: 409);
            if (action != "start" && b!.Confirm != true) return Results.BadRequest(new { error = "confirm required" });
            var tok = d.Opt.MigrateTokenFor(n.Host.Address);
            if (tok == null) return Results.Json(new { error = $"no write token configured for {n.Name}" }, statusCode: 409);

            using var pve = new PveClient(n.Host.Address, tok, n.Host.SkipTlsVerification, TimeSpan.FromSeconds(30));
            var who = ctx.User.FindFirstValue(ClaimTypes.Name) ?? "?";
            try
            {
                var form = action == "shutdown" ? new[] { new KeyValuePair<string, string>("timeout", "180") } : null;
                var upid = (await pve.PostAsync($"/nodes/{PveClient.Enc(n.Name)}/{type}/{vmid}/status/{action}", form, ctx.RequestAborted)).GetString();
                await Audit(d, who, $"guest.{action}", upid, new { guest = g.Name, type, vmid, node = n.Name, ok = true });
                var task = reg.Add(new ActivityRegistry.PowerTask
                {
                    Upid = upid!, HostId = n.Host.Id, Node = n.Name, Guest = g.Name, GuestType = type, Vmid = vmid, Action = action!, By = who,
                });
                d.Hub.Broadcast("activity", new { kind = "power", id = task.Id });
                _ = FollowAsync(d.Store, d.Collector, d.Hub, n, task);
                return Results.Ok(new { ok = true, upid, id = task.Id, message = $"{action} sent to {g.Name}" });
            }
            catch (PveException ex)
            {
                await Audit(d, who, $"guest.{action}", null, new { guest = g.Name, type, vmid, node = n.Name, ok = false, error = ex.Message });
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // status of a task started above, read with the node's read token
        app.MapGet("/api/inventory/nodes/{hostId}/tasks/{upid}", async (string hostId, string upid, Deps d, HttpContext ctx) =>
        {
            var h = d.Store.GetHost(hostId);
            var tok = h != null ? FleetCollector.ReadToken(d.Store, h) : null;
            var node = d.State.For(hostId).LastGood?.Node;
            if (h == null || tok == null || node == null) return Results.NotFound();
            using var pve = new PveClient(h.Address, tok, h.SkipTlsVerification);
            try
            {
                var st = await pve.GetAsync($"/nodes/{PveClient.Enc(node)}/tasks/{PveClient.Enc(upid)}/status", ctx.RequestAborted);
                return Results.Ok(new { status = Pj.Str(st, "status"), exitstatus = Pj.Str(st, "exitstatus") });
            }
            catch (PveException ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
        });
    }

    // When a power task finishes, re-read the fleet so the tree shows the new state without waiting a cycle.
    // Deps is request-scoped, so only singletons cross into this background follow-up.
    private static async Task FollowAsync(Store store, FleetCollector collector, EventHub hub, NodeCtx n, ActivityRegistry.PowerTask task)
    {
        try
        {
            var tok = FleetCollector.ReadToken(store, n.Host);
            if (tok == null) return;
            using var pve = new PveClient(n.Host.Address, tok, n.Host.SkipTlsVerification);
            var until = DateTimeOffset.UtcNow.AddMinutes(10);
            while (DateTimeOffset.UtcNow < until)
            {
                var st = await pve.GetAsync($"/nodes/{PveClient.Enc(n.Name)}/tasks/{PveClient.Enc(task.Upid)}/status", CancellationToken.None);
                if (Pj.Str(st, "status") == "stopped") { task.ExitStatus = Pj.Str(st, "exitstatus") ?? "stopped"; break; }
                await Task.Delay(2000);
            }
            task.ExitStatus ??= "no result after 10 min — check the task in Proxmox";
            task.FinishedAt = DateTimeOffset.UtcNow;
            hub.Broadcast("activity", new { kind = "power", id = task.Id, done = true });
            await collector.CollectOnceAsync(CancellationToken.None);
        }
        catch (Exception ex) { task.ExitStatus ??= "lost track: " + ex.Message; task.FinishedAt ??= DateTimeOffset.UtcNow; hub.Broadcast("activity", new { kind = "power", id = task.Id, done = true }); }
    }

    private static object NodeDto(NodeCtx n, FleetCtx f)
    {
        var r = n.Reading;
        var running = r == null ? [] : FleetAnalysis.RunningGuests(r).ToList();
        return new
        {
            hostId = n.Host.Id, node = n.Name, address = n.Host.Address,
            reachable = n.Latest?.Ok, error = n.Latest?.Ok == false ? n.Latest.Error : null,
            stale = n.Stale, ageSeconds = n.AgeSeconds, takenAt = r?.TakenAt,
            cpuModel = r?.CpuModel, sockets = r?.Sockets, cores = r?.Cores, threads = r?.Threads,
            load15 = r?.Load15, uptimeSeconds = r?.UptimeSeconds, pveVersion = r?.PveVersion,
            memTotal = r?.MemTotal, memAvailable = r?.MemAvailable, arcCap = n.Probe?.ArcMax,
            vcpuCommitted = running.Sum(g => g.Vcpus ?? 0),
            unlimitedContainers = running.Count(g => g.CpuUnlimited),
            memAllocated = running.Sum(g => g.MaxMem ?? 0),
            guests = r?.Guests.Count(g => !g.Template), running = running.Count,
            hardware = n.Hardware?.Status.ToString(),
            maintenance = n.InMaintenance(f.Now) ? new { n.Maintenance!.Until, n.Maintenance.Reason } : null,
            hasWriteToken = n.HasMigrateToken,
            storages = r?.Storages.Where(s => s.HoldsGuests && s.Active && s.Enabled)
                        .Select(s => new { s.Id, s.Type, s.Total, s.Used, s.Avail, s.Media, s.MediaNote, s.ZfsPool }).ToList(),
        };
    }

    private static object GuestDto(NodeCtx n, GuestReading g, FleetCtx f)
    {
        var r = n.Reading!;
        var running = FleetAnalysis.RunningGuests(r).ToList();
        var others = running.Where(x => !(x.Vmid == g.Vmid && x.Type == g.Type)).ToList();
        double? Share(long? part, long? whole) => part != null && whole > 0 ? (double)part.Value / whole.Value : null;
        var stores = r.Storages.ToDictionary(s => s.Id);
        var os = OsClass(g);
        return new
        {
            hostId = n.Host.Id, node = n.Name, address = n.Host.Address,
            g.Vmid, g.Type, g.Name, g.Status, g.Lock, g.Template, running = g.Running,
            g.UptimeSeconds, g.Onboot, g.Tags, g.Description,
            os = new { cls = os.Cls, label = os.Label, g.OsType, g.OsName },
            ips = g.Ips, g.IpSource, g.IpError,
            services = ServiceTags.Select(st => (st, tag: g.Tags.FirstOrDefault(t => st.Tags.Contains(t.ToLowerInvariant()))))
                .Where(x => x.tag != null).Select(x => new { icon = x.st.Icon, label = x.st.Label, source = $"Proxmox tag '{x.tag}'" }).ToList(),
            cpu = new
            {
                vcpus = g.Vcpus, unlimited = g.CpuUnlimited, busy = g.CpuBusy,
                nodeThreads = r.Threads,
                share = g.Running ? Share(g.Vcpus, r.Threads) : null,
                othersShare = Share(others.Sum(x => (long)(x.Vcpus ?? 0)), r.Threads),
            },
            ram = new
            {
                allocated = g.MaxMem, used = g.Running ? g.Mem : null, nodeTotal = r.MemTotal,
                share = g.Running ? Share(g.MaxMem, r.MemTotal) : null,
                othersShare = Share(others.Sum(x => x.MaxMem ?? 0), r.MemTotal),
            },
            disks = g.Disks.Where(dk => !dk.CloudInit).Select(dk =>
            {
                stores.TryGetValue(dk.Storage, out var s);
                return new
                {
                    dk.Key, dk.Storage, dk.VirtualBytes, dk.AllocatedBytes,
                    poolTotal = s?.Total, poolUsed = s?.Used, media = s?.Media, mediaNote = s?.MediaNote, poolName = s?.ZfsPool ?? dk.Storage,
                    share = Share(dk.AllocatedBytes, s?.Total),
                    othersShare = s?.Used != null && dk.AllocatedBytes != null && s.Total > 0
                        ? Math.Max(0, (double)(s.Used.Value - dk.AllocatedBytes.Value) / s.Total.Value) : (double?)null,
                };
            }).ToList(),
            diskVirtual = g.Disks.Where(dk => !dk.CloudInit).All(dk => dk.VirtualBytes != null) ? g.Disks.Where(dk => !dk.CloudInit).Sum(dk => dk.VirtualBytes!.Value) : (long?)null,
            diskAllocated = g.Disks.Where(dk => !dk.CloudInit).All(dk => dk.AllocatedBytes != null) ? g.Disks.Where(dk => !dk.CloudInit).Sum(dk => dk.AllocatedBytes!.Value) : (long?)null,
            rules = f.Rules.Where(x => FleetAnalysis.Members(x).Contains(g.Name, StringComparer.OrdinalIgnoreCase)).Select(x => new { x.Kind, x.Name }).ToList(),
            blockers = g.MigrationBlockers,
            consoleUrl = $"https://{n.Host.Address}:8006/?console={(g.Type == "lxc" ? "lxc" : "kvm")}&novnc=1&vmid={g.Vmid}&vmname={Uri.EscapeDataString(g.Name)}&node={Uri.EscapeDataString(n.Name)}&resize=off",
            stale = n.Stale,
        };
    }

    // From the guest's own config (ostype) and agent — never from its name.
    private static (string? Cls, string? Label) OsClass(GuestReading g)
    {
        var t = g.OsType?.ToLowerInvariant();
        if (t == null) return (null, g.OsName);
        if (t.StartsWith("win") || t is "w2k" or "w2k3" or "w2k8" or "wxp" or "wvista") return ("win", g.OsName ?? "Windows (ostype " + t + ")");
        if (t is "l24" or "l26" or "ubuntu" or "debian" or "centos" or "fedora" or "alpine" or "archlinux" or "opensuse" or "gentoo" or "devuan" or "nixos" or "unmanaged")
            return ("lnx", g.OsName ?? (g.Type == "lxc" ? $"Linux container ({t})" : "Linux"));
        return (null, g.OsName ?? t);
    }

    private static long? Sum(IEnumerable<long?> xs) { var l = xs.ToList(); return l.Count == 0 || l.Any(x => x == null) ? null : l.Sum(x => x!.Value); }

    private static async Task Audit(Deps d, string who, string verb, string? id, object detail)
    {
        try
        {
            using var s = d.Scopes.CreateScope();
            await s.ServiceProvider.GetRequiredService<IUserStore>().WriteAuditAsync(new AuditEventEntity
            {
                Timestamp = DateTimeOffset.UtcNow, Username = who, Verb = verb, Allowed = true, CorrelationId = id,
                Detail = JsonSerializer.Serialize(detail, J),
            });
        }
        catch { }
    }
}

public record PowerDto(string? Action, bool? Confirm = null);
