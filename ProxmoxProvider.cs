using System.Net.Http.Json;
using System.Text.Json.Serialization;
using VMentory.Core;

namespace VMentory.Web;

// Proxmox VE provider — drives the PVE REST API (ENG-0009).
// Auth: PVEAPIToken header (user@realm!tokenid=uuid), stored as per-host cred (password field).
// TLS: self-signed PVE cert accepted when host.SkipTlsVerification is set.
// One registered Host = one PVE node. Cluster peers are registered separately (slice 4 scope).
public sealed class ProxmoxProvider(Store store) : IVirtualizationProvider
{
    public PlatformKind Platform => PlatformKind.Proxmox;

    public ProviderCapabilities Capabilities { get; } =
        new(ProviderCapability.Inventory | ProviderCapability.LiveStats);

    // Lightweight check: GET /version verifies connectivity + token; GET /nodes populates basic info.
    public async Task<(bool Ok, string Error)> QuickConnectAsync(Host host, CancellationToken ct = default)
    {
        var token = GetToken(host);
        if (token == null) return (false, "No API token configured — add host with a PVEAPIToken");

        using var http = BuildClient(host, token);
        try
        {
            var ver = await GetPveAsync<PveVersion>(http, "/api2/json/version", ct);
            if (ver == null) return (false, "Empty response from /version");

            var nodes = await GetPveAsync<List<PveNode>>(http, "/api2/json/nodes", ct);
            var node = nodes?.FirstOrDefault(n => n.Status == "online");
            if (node != null)
            {
                host.Fqdn        = node.Node;
                host.OsCaption   = "Proxmox VE";
                host.OsVersion   = ver.Version ?? "";
                host.TotalCores  = node.MaxCpu;
                host.TotalRamGb  = Math.Round(node.MaxMem / 1024.0 / 1024 / 1024, 2);
                host.Reachability.Auth = AuthState.Ok;
                host.Reachability.ErrorDetail = null;
            }

            return (true, "");
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode == 401 || (int?)ex.StatusCode == 403)
        {
            return (false, "API token rejected — check token ID and secret");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // Full inventory: node hardware + all QEMU VMs + LXC containers.
    public async Task<(bool Ok, string Error)> ScanAsync(Host host, CancellationToken ct = default)
    {
        var token = GetToken(host);
        if (token == null) return (false, "No API token configured");

        using var http = BuildClient(host, token);
        try
        {
            var nodes = await GetPveAsync<List<PveNode>>(http, "/api2/json/nodes", ct);
            var node = nodes?.FirstOrDefault(n => n.Status == "online");
            if (node == null) return (false, "No online nodes found");

            var status = await GetPveAsync<PveNodeStatus>(http, $"/api2/json/nodes/{node.Node}/status", ct);
            var qemu   = await GetPveAsync<List<PveGuest>>(http, $"/api2/json/nodes/{node.Node}/qemu", ct) ?? [];
            var lxc    = await GetPveAsync<List<PveGuest>>(http, $"/api2/json/nodes/{node.Node}/lxc", ct)  ?? [];

            host.Fqdn             = node.Node;
            host.OsCaption        = "Proxmox VE";
            host.OsVersion        = ParsePveVersion(status?.PveVersion);
            host.CpuModel         = status?.CpuInfo?.Model ?? "";
            host.SocketCount      = status?.CpuInfo?.Sockets ?? 1;
            host.TotalCores       = (status?.CpuInfo?.Cores ?? 1) * (status?.CpuInfo?.Sockets ?? 1);
            host.TotalLogicalProcs = status?.CpuInfo?.Cpus ?? host.TotalCores;
            host.TotalRamGb       = Math.Round((status?.Memory?.Total ?? node.MaxMem) / 1024.0 / 1024 / 1024, 2);
            host.LastScanned      = DateTimeOffset.UtcNow;
            host.Reachability.Auth = AuthState.Ok;

            var vms = new List<Vm>(qemu.Count + lxc.Count);
            foreach (var g in qemu.Where(g => g.Template != 1))
                vms.Add(MapGuest(g, host.Id, isLxc: false));
            foreach (var g in lxc.Where(g => g.Template != 1))
                vms.Add(MapGuest(g, host.Id, isLxc: true));

            host.Vms = vms;
            return (true, "");
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode == 401 || (int?)ex.StatusCode == 403)
        {
            return (false, "API token rejected");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private string? GetToken(Host host)
    {
        var creds = store.GetEffectiveCreds(host);
        var pw = creds?.GetPassword();
        return string.IsNullOrWhiteSpace(pw) ? null : pw;
    }

    private static HttpClient BuildClient(Host host, string token)
    {
        var handler = new HttpClientHandler();
        if (host.SkipTlsVerification)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{host.Address}:8006"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        // PVE's token scheme ("PVEAPIToken=user@realm!tokenid=uuid") is non-standard — it uses '='
        // instead of a space and contains '!', so the validating Add() throws FormatException before
        // the request is sent. TryAddWithoutValidation sends it verbatim, which is what PVE expects.
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"PVEAPIToken={token}");
        return http;
    }

    private static async Task<T?> GetPveAsync<T>(HttpClient http, string path, CancellationToken ct)
    {
        var resp = await http.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        var wrapper = await resp.Content.ReadFromJsonAsync<PveResponse<T>>(cancellationToken: ct);
        return wrapper == null ? default : wrapper.Data;
    }

    private static Vm MapGuest(PveGuest g, string hostId, bool isLxc) => new()
    {
        HostId        = hostId,
        Name          = g.Name ?? $"VM {g.Vmid}",
        State         = g.Status ?? "unknown",
        VCpuCount     = g.Cpus,
        AssignedRamMb = (long)(g.Maxmem / 1024.0 / 1024),
        StartupRamMb  = (long)(g.Maxmem / 1024.0 / 1024),
        Generation    = isLxc ? 0 : 2,
        GuestOs       = isLxc ? "LXC" : "",
        Uptime        = g.Uptime > 0 ? FormatUptime(g.Uptime) : "",
    };

    private static string FormatUptime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    // "pve-manager/8.2.2/..." → "8.2.2"
    private static string ParsePveVersion(string? raw)
    {
        if (raw == null) return "";
        var parts = raw.Split('/');
        return parts.Length >= 2 ? parts[1] : raw;
    }
}

// ── PVE REST API response shapes ─────────────────────────────────────────────

internal record PveResponse<T>([property: JsonPropertyName("data")] T? Data);

internal record PveVersion(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("release")] string? Release);

internal record PveNode(
    [property: JsonPropertyName("node")]   string Node,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("maxcpu")] int    MaxCpu,
    [property: JsonPropertyName("maxmem")] long   MaxMem,
    [property: JsonPropertyName("uptime")] long   Uptime);

internal record PveNodeStatus(
    [property: JsonPropertyName("cpuinfo")]     PveCpuInfo?  CpuInfo,
    [property: JsonPropertyName("memory")]      PveMemory?   Memory,
    [property: JsonPropertyName("pve-version")] string?      PveVersion);

internal record PveCpuInfo(
    [property: JsonPropertyName("model")]   string? Model,
    [property: JsonPropertyName("cpus")]    int     Cpus,
    [property: JsonPropertyName("cores")]   int     Cores,
    [property: JsonPropertyName("sockets")] int     Sockets);

internal record PveMemory(
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("used")]  long Used);

internal record PveGuest(
    [property: JsonPropertyName("vmid")]     int    Vmid,
    [property: JsonPropertyName("name")]     string? Name,
    [property: JsonPropertyName("status")]   string? Status,
    [property: JsonPropertyName("cpus")]     int    Cpus,
    [property: JsonPropertyName("maxmem")]   long   Maxmem,
    [property: JsonPropertyName("mem")]      long   Mem,
    [property: JsonPropertyName("uptime")]   long   Uptime,
    [property: JsonPropertyName("template")] int    Template);
