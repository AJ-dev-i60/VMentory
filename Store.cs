using System.Collections.Concurrent;

namespace HyperInventory;

// Thread-safe in-memory state — no disk writes, no persistence.
public class Store
{
    private readonly ConcurrentDictionary<string, Host> _hosts = new();
    private readonly object _credsLock = new();
    private Credentials? _globalCreds;
    private SessionDiff _diff = new();
    private readonly object _diffLock = new();

    public bool HasGlobalCreds { get; private set; }

    public void SetGlobalCredentials(string username, string password)
    {
        lock (_credsLock)
        {
            _globalCreds?.Dispose();
            _globalCreds = new Credentials(username, password);
            HasGlobalCreds = true;
        }
    }

    public Credentials? GetEffectiveCreds(Host host)
    {
        lock (_credsLock)
        {
            if (!host.UseGlobalCreds && host.PerHostCreds != null)
                return host.PerHostCreds;
            return _globalCreds;
        }
    }

    public void AddHost(Host host) => _hosts[host.Id] = host;

    public bool RemoveHost(string id)
    {
        var removed = _hosts.TryRemove(id, out var host);
        if (removed) host?.PerHostCreds?.Dispose();
        return removed;
    }

    public Host? GetHost(string id) => _hosts.GetValueOrDefault(id);

    public IReadOnlyList<Host> GetAllHosts() => [.. _hosts.Values];

    public void UpdateHost(string id, Action<Host> update)
    {
        if (_hosts.TryGetValue(id, out var host))
            update(host);
    }

    public ClusterTotals ComputeTotals()
    {
        var hosts = GetAllHosts();
        var totals = new ClusterTotals
        {
            HostCount = hosts.Count,
            TotalCores = hosts.Sum(h => h.TotalCores),
            TotalRamGb = hosts.Sum(h => h.TotalRamGb),
            TotalVCpu = hosts.SelectMany(h => h.Vms).Sum(v => v.VCpuCount),
            TotalVmRamGb = hosts.SelectMany(h => h.Vms).Sum(v => v.AssignedRamMb / 1024.0),
        };

        totals.RamCommitRatio = totals.TotalRamGb > 0
            ? Math.Round(totals.TotalVmRamGb / totals.TotalRamGb, 2) : 0;
        totals.CpuCommitRatio = totals.TotalCores > 0
            ? Math.Round((double)totals.TotalVCpu / totals.TotalCores, 2) : 0;

        return totals;
    }

    public void RecordDiff(List<Host> previous)
    {
        var diff = new SessionDiff();
        var current = GetAllHosts();

        var prevVms = previous
            .SelectMany(h => h.Vms.Select(v => (h.Id, v)))
            .ToDictionary(x => $"{x.Id}:{x.v.Name}", x => x.v);

        var currVms = current
            .SelectMany(h => h.Vms.Select(v => (h.Id, v)))
            .ToDictionary(x => $"{x.Id}:{x.v.Name}", x => (x.Id, x.v));

        foreach (var (key, (hostId, vm)) in currVms)
        {
            if (!prevVms.ContainsKey(key))
                diff.Added.Add(new DiffEntry { HostId = hostId, VmName = vm.Name });
        }

        foreach (var (key, vm) in prevVms)
        {
            if (!currVms.ContainsKey(key))
            {
                var parts = key.Split(':', 2);
                diff.Removed.Add(new DiffEntry { HostId = parts[0], VmName = parts[1] });
            }
        }

        foreach (var (key, (hostId, vm)) in currVms)
        {
            if (!prevVms.TryGetValue(key, out var prev)) continue;

            if (prev.VCpuCount != vm.VCpuCount)
                diff.Changed.Add(new DiffEntry
                {
                    HostId = hostId, VmName = vm.Name, Field = "vCPU",
                    OldVal = prev.VCpuCount.ToString(), NewVal = vm.VCpuCount.ToString()
                });

            if (prev.AssignedRamMb != vm.AssignedRamMb)
                diff.Changed.Add(new DiffEntry
                {
                    HostId = hostId, VmName = vm.Name, Field = "RAM",
                    OldVal = $"{prev.AssignedRamMb} MB", NewVal = $"{vm.AssignedRamMb} MB"
                });
        }

        lock (_diffLock) { _diff = diff; }
    }

    public SessionDiff GetDiff() { lock (_diffLock) { return _diff; } }

    public void ClearDiff() { lock (_diffLock) { _diff = new SessionDiff(); } }

    public void ClearAll()
    {
        foreach (var h in _hosts.Values) h.PerHostCreds?.Dispose();
        _hosts.Clear();
        lock (_credsLock) { _globalCreds?.Dispose(); _globalCreds = null; HasGlobalCreds = false; }
        lock (_diffLock) { _diff = new SessionDiff(); }
    }
}
