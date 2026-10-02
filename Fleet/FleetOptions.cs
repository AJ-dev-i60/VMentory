using System.Diagnostics;

namespace VMentory.Web.Fleet;

// ENG-0016 env contract. Everything optional: with none of it set the fleet view shows whatever
// Proxmox hosts are registered (read path only) and every write verb reports itself unconfigured.
//
//   VMENTORY_SEED_PVE_HOSTS        extra Proxmox nodes to register on start, one per line or ';':
//                                  "address|user@realm!tokenid=secret|display name" (read-only token)
//   VMENTORY_PVE_MIGRATE_TOKENS    write tokens, "address|user@realm!tokenid=secret" per entry. Used
//                                  ONLY by move/drain; a node without one cannot be a source or target.
//   VMENTORY_PVE_PROBE_KEY         base64 of the OpenSSH private key whose public half is installed on
//                                  each node with restrict + a forced command (ops/provision-fleet-nodes.sh)
//   VMENTORY_PVE_PROBE_USER        default root
//   VMENTORY_FLEET_INTERVAL        REST poll, seconds (default 60, min 15)
//   VMENTORY_FLEET_PROBE_INTERVAL  SSH probe, seconds (default 600, min 60)
//   VMENTORY_FLEET_SEED=0          skip the first-run rule seed
public sealed class FleetOptions
{
    public int IntervalSeconds { get; init; } = 60;
    public int ProbeIntervalSeconds { get; init; } = 600;
    public bool SeedRules { get; init; } = true;
    public Dictionary<string, string> MigrateTokens { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ProbeKeyPath { get; init; }
    public string ProbeUser { get; init; } = "root";
    public string? KnownHostsPath { get; init; }
    public List<(string Address, string Token, string? Name)> SeedHosts { get; init; } = [];

    public bool ProbeEnabled => ProbeKeyPath != null;
    public string? MigrateTokenFor(string address) => MigrateTokens.TryGetValue(address, out var t) ? t : null;

    public static FleetOptions FromEnvironment(string dataDir)
    {
        var interval = int.TryParse(Environment.GetEnvironmentVariable("VMENTORY_FLEET_INTERVAL"), out var i) ? Math.Max(15, i) : 60;
        var probeInterval = int.TryParse(Environment.GetEnvironmentVariable("VMENTORY_FLEET_PROBE_INTERVAL"), out var p) ? Math.Max(60, p) : 600;

        var migrate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries("VMENTORY_PVE_MIGRATE_TOKENS"))
        {
            var parts = entry.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0] != "" && parts[1] != "") migrate[parts[0]] = parts[1];
            else DevLog.Warn("[FLEET] ignored a malformed VMENTORY_PVE_MIGRATE_TOKENS entry (want address|token)");
        }

        var seeds = new List<(string, string, string?)>();
        foreach (var entry in Entries("VMENTORY_SEED_PVE_HOSTS"))
        {
            var parts = entry.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts[0] != "" && parts[1] != "")
                seeds.Add((parts[0], parts[1], parts.Length > 2 && parts[2] != "" ? parts[2] : null));
            else DevLog.Warn("[FLEET] ignored a malformed VMENTORY_SEED_PVE_HOSTS entry (want address|token|name)");
        }

        string? keyPath = null, knownHosts = null;
        var keyB64 = Environment.GetEnvironmentVariable("VMENTORY_PVE_PROBE_KEY");
        if (!string.IsNullOrWhiteSpace(keyB64) && !string.IsNullOrEmpty(dataDir))
        {
            try
            {
                var bytes = Convert.FromBase64String(keyB64.Trim());
                keyPath = Path.Combine(dataDir, "fleet-probe-key");
                File.WriteAllBytes(keyPath, bytes);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                knownHosts = Path.Combine(dataDir, "fleet-known_hosts");
            }
            catch (Exception ex)
            {
                keyPath = null;
                DevLog.Warn($"[FLEET] VMENTORY_PVE_PROBE_KEY unusable ({ex.Message}) — ARC cap and service tag will show as unknown");
            }
        }

        return new FleetOptions
        {
            IntervalSeconds = interval,
            ProbeIntervalSeconds = probeInterval,
            SeedRules = Environment.GetEnvironmentVariable("VMENTORY_FLEET_SEED") != "0",
            MigrateTokens = migrate,
            ProbeKeyPath = keyPath,
            ProbeUser = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VMENTORY_PVE_PROBE_USER")) ? "root" : Environment.GetEnvironmentVariable("VMENTORY_PVE_PROBE_USER")!.Trim(),
            KnownHostsPath = knownHosts,
            SeedHosts = seeds,
        };
    }

    private static IEnumerable<string> Entries(string env) =>
        (Environment.GetEnvironmentVariable(env) ?? "")
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Runs the forced-command probe. The key can only run /usr/local/sbin/vmentory-probe, so whatever
    // command we pass is ignored by sshd; we send none.
    public async Task<VMentory.Core.Fleet.ProbeReading> ProbeAsync(string address, CancellationToken ct)
    {
        var r = new VMentory.Core.Fleet.ProbeReading { TakenAt = DateTimeOffset.UtcNow };
        if (ProbeKeyPath == null) { r.Error = "probe key not configured"; return r; }
        try
        {
            var psi = new ProcessStartInfo("ssh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var a in new[] { "-i", ProbeKeyPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=8",
                                      "-o", "StrictHostKeyChecking=accept-new", "-o", $"UserKnownHostsFile={KnownHostsPath}",
                                      "-o", "IdentitiesOnly=yes", "-T", $"{ProbeUser}@{address}" })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            if (proc.ExitCode != 0) { r.Error = $"ssh exit {proc.ExitCode}: {(await stderr).Trim()}"; return r; }
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var tab = line.Split('\t');
                if (tab.Length == 3)
                {
                    if (tab[0] is "zfs" or "lvm" or "file" && long.TryParse(tab[2], out var bytes))
                        r.Allocated[$"{tab[0]}:{tab[1]}"] = bytes;
                    else if (tab[0] == "snap") { r.MigrationSnapshots.Add(tab[2]); }
                    else if (tab[0] == "media")
                    {
                        var kv = System.Text.RegularExpressions.Regex.Matches(tab[2], "(\\w+)=\"([^\"]*)\"")
                            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
                        if (!r.Media.TryGetValue(tab[1], out var list)) r.Media[tab[1]] = list = [];
                        list.Add(new VMentory.Core.Fleet.ProbeDisk(kv.GetValueOrDefault("NAME", ""),
                            kv.GetValueOrDefault("ROTA") switch { "1" => true, "0" => false, _ => null },
                            kv.GetValueOrDefault("MODEL", "").Trim(), kv.GetValueOrDefault("TRAN", "")));
                    }
                    else if (tab[0] == "cfg" && tab[2].IndexOf('=') is var e and > 0)
                    {
                        if (!r.StorageCfg.TryGetValue(tab[1], out var kv)) r.StorageCfg[tab[1]] = kv = new(StringComparer.Ordinal);
                        kv[tab[2][..e]] = tab[2][(e + 1)..];
                    }
                    continue;
                }
                line = line.Trim();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq]; var val = line[(eq + 1)..].Trim();
                if (val == "") continue;
                switch (key)
                {
                    case "arc_c_max": if (long.TryParse(val, out var arc)) r.ArcMax = arc; break;
                    case "serial": r.Serial = val; break;
                    case "product": r.Product = val; break;
                }
            }
            r.Ok = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { r.Error = "probe timed out"; }
        catch (Exception ex) { r.Error = ex.Message; }
        return r;
    }
}
