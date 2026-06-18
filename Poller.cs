using VMentory.Core;

namespace VMentory.Web;

// Background service: re-checks reachability every 30s. Does NOT re-scan inventory.
public class Poller(Store store, EventHub hub, AppConfig config, ProviderRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            await PollAllAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    public async Task PollAllAsync(CancellationToken ct = default)
    {
        var hosts = store.GetAllHosts();
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (host, innerCt) => await PollHostAsync(host, innerCt));
    }

    private async Task PollHostAsync(Host host, CancellationToken ct)
    {
        var r = host.Reachability;
        r.CheckedAt = DateTimeOffset.UtcNow;
        r.Icmp = await ReachabilityChecker.PingAsync(host.Address, TimeSpan.FromSeconds(2), ct);

        if (host.Platform == PlatformKind.Proxmox)
        {
            // Proxmox: check PVE API port (8006) then verify token via GET /version.
            r.WinRm = await ReachabilityChecker.TestTcpPortAsync(host.Address, 8006, TimeSpan.FromSeconds(3), ct);
            if (r.WinRm)
            {
                var (ok, err) = await registry.For(PlatformKind.Proxmox).QuickConnectAsync(host, ct);
                if (!ok) { r.Auth = AuthState.Failed; r.ErrorDetail = err; }
                // on success, QuickConnectAsync already set r.Auth = Ok + r.ErrorDetail = null
            }
            else
            {
                r.Auth = AuthState.Unknown;
                r.ErrorDetail = r.Icmp ? "PVE API port (8006) unreachable" : "Host unreachable";
            }
        }
        else
        {
            // HyperV: WinRM port + auth check.
            r.WinRm = await ReachabilityChecker.TestTcpPortAsync(host.Address, config.WinRmPort, TimeSpan.FromSeconds(3), ct);
            if (r.WinRm)
            {
                var creds = store.GetEffectiveCreds(host);
                if (creds != null)
                {
                    var (authState, err) = await ReachabilityChecker.TestWinRmAuthAsync(
                        host.Address, creds, config.WinRmPort, TimeSpan.FromSeconds(10), ct);
                    r.Auth = authState;
                    r.ErrorDetail = err;
                }
                else
                {
                    r.Auth = AuthState.Unknown;
                    r.ErrorDetail = "No credentials configured";
                }
            }
            else
            {
                r.Auth = AuthState.Unknown;
                r.ErrorDetail = r.Icmp ? "WinRM port unreachable" : "Host unreachable";
            }
        }

        hub.Broadcast("reachability", new { hostId = host.Id, reachability = r });
    }
}
