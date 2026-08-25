using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VMentory.Core.Estate;

namespace VMentory.Web;

// Dell OpenManage Enterprise — the out-of-band hardware monitor (ENG-0015).
// OME watches every iDRAC and rolls each server's hardware health into one console; VMentory reads
// that roll-up plus the two inventories that carry the actionable detail (physical disks, PSUs) and
// turns them into HardwareFaults. Everything here is hardware: the OS-level view is the provider's.
//
// Quirks of the 3.10 build this was written against, all verified live:
//   • auth is POST SessionService/Sessions → X-Auth-Token header; sessions expire → re-auth on 401
//   • status codes are numeric strings/ints: 1000 OK · 3000 Warning · 4000 Critical · 2000 Unknown
//   • $select is unsupported (400); OData filters need spaces %20-escaped
//   • IDSDM/SDCard subsystems read 2000 on every box — an absent SD module, not a fault
public sealed class OmeOptions
{
    public string? Url { get; init; }
    public string? User { get; init; }
    public string? Password { get; init; }
    public bool SkipTls { get; init; }
    public int IntervalSeconds { get; init; } = 300;

    public bool Enabled => !string.IsNullOrWhiteSpace(Url)
                        && !string.IsNullOrWhiteSpace(User)
                        && !string.IsNullOrWhiteSpace(Password);

    public static OmeOptions FromEnvironment()
    {
        var interval = int.TryParse(Environment.GetEnvironmentVariable("VMENTORY_OME_INTERVAL"), out var s) && s >= 30 ? s : 300;
        return new OmeOptions
        {
            Url      = Environment.GetEnvironmentVariable("VMENTORY_OME_URL")?.TrimEnd('/'),
            User     = Environment.GetEnvironmentVariable("VMENTORY_OME_USER"),
            Password = Environment.GetEnvironmentVariable("VMENTORY_OME_PASSWORD"),
            SkipTls  = Environment.GetEnvironmentVariable("VMENTORY_OME_SKIP_TLS") is "1" or "true" or "yes",
            IntervalSeconds = interval,
        };
    }
}

public sealed class OmeClient : IDisposable
{
    private readonly OmeOptions _opt;
    private readonly HttpClient _http;
    private string? _token;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public OmeClient(OmeOptions opt)
    {
        _opt = opt;
        var handler = new HttpClientHandler();
        if (opt.SkipTls)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(handler) { BaseAddress = new Uri((opt.Url ?? "https://localhost") + "/"), Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<HardwareSnapshot> ReadAsync(CancellationToken ct)
    {
        var snap = new HardwareSnapshot { TakenAt = DateTimeOffset.UtcNow, Source = "ome" };
        try
        {
            var devices = await GetAsync<OmeList<OmeDevice>>("api/DeviceService/Devices", ct);
            var servers = (devices?.Value ?? []).Where(d => d.Type == 1000).ToList();

            // OME is slow per call and locks accounts on auth storms — modest parallelism only.
            var sem = new SemaphoreSlim(3, 3);
            var tasks = servers.Select(async d =>
            {
                await sem.WaitAsync(ct);
                try { return await ReadDeviceAsync(d, ct); }
                finally { sem.Release(); }
            });
            snap.Devices = (await Task.WhenAll(tasks)).OrderBy(d => d.ManagementIp).ToList();
            snap.Ok = true;
        }
        catch (Exception ex)
        {
            snap.Ok = false;
            snap.Error = ex.Message;
        }
        return snap;
    }

    private async Task<HardwareDevice> ReadDeviceAsync(OmeDevice d, CancellationToken ct)
    {
        var dev = new HardwareDevice
        {
            MonitorId    = d.Id,
            ServiceTag   = d.DeviceServiceTag ?? "",
            DeviceName   = d.DeviceName ?? "",
            Model        = d.Model ?? "",
            ManagementIp = d.DeviceManagement?.FirstOrDefault()?.NetworkAddress ?? "",
            Status       = MapStatus(d.Status),
            Connected    = d.ConnectionState,
        };
        var tag = dev.ServiceTag;

        if (!dev.Connected)
            dev.Faults.Add(new HardwareFault($"unreachable", FaultSeverity.Critical, "iDRAC",
                "The monitor has lost contact with this iDRAC — hardware health is unknown"));

        // Subsystem roll-ups. Unknown (2000) is skipped: on this estate it only ever means an absent SD card.
        var subs = await GetAsync<OmeList<OmeSubsystem>>($"api/DeviceService/Devices({d.Id})/SubSystemHealth", ct);
        foreach (var s in subs?.Value ?? [])
        {
            var st = MapStatus(s.RollupStatus);
            if (st == HardwareStatus.Unknown) continue;
            dev.Subsystems[s.SubSystem ?? "?"] = st;
        }

        // Physical disks — the two faults that matter are predictive failure and Foreign config.
        var disks = await GetAsync<OmeInventory<OmeDisk>>($"api/DeviceService/Devices({d.Id})/InventoryDetails('serverArrayDisks')", ct);
        var foreign = new List<string>();
        foreach (var disk in disks?.InventoryInfo ?? [])
        {
            var bay = disk.SlotNumber?.ToString() ?? BayFromName(disk.DiskNumber);
            var size = FormatSize(disk.Size);
            if (string.Equals(disk.PredictiveFailureState, "Yes", StringComparison.OrdinalIgnoreCase))
                dev.Faults.Add(new HardwareFault($"disk-predfail:{bay}", FaultSeverity.Critical, $"Disk bay {bay}",
                    $"{disk.ModelNumber?.Trim()} {size} — predictive failure, RAID {disk.RaidStatus}"));
            else if (string.Equals(disk.RaidStatus, "Foreign", StringComparison.OrdinalIgnoreCase))
                foreign.Add($"{bay} ({disk.ModelNumber?.Trim()} {size})");
            else if (MapStatus(disk.Status) is HardwareStatus.Warning or HardwareStatus.Critical)
                dev.Faults.Add(new HardwareFault($"disk-status:{bay}", FaultSeverity.Warning, $"Disk bay {bay}",
                    $"{disk.ModelNumber?.Trim()} {size} — {disk.StatusString}, RAID {disk.RaidStatus}"));
        }
        if (foreign.Count > 0)
            dev.Faults.Add(new HardwareFault("disk-foreign", FaultSeverity.Warning, "Foreign disks",
                $"{foreign.Count} disk{(foreign.Count == 1 ? "" : "s")} with a foreign RAID config in bay{(foreign.Count == 1 ? "" : "s")} {string.Join(", ", foreign)}"));

        // Power supplies.
        var psus = await GetAsync<OmeInventory<OmePsu>>($"api/DeviceService/Devices({d.Id})/InventoryDetails('serverPowerSupplies')", ct);
        var psuIndex = 0;
        foreach (var p in psus?.InventoryInfo ?? [])
        {
            psuIndex++;
            var n = BayFromName(p.Name) is var num && num != "?" ? num : psuIndex.ToString();
            if (MapStatus(p.Status) is HardwareStatus.Warning or HardwareStatus.Critical
                || (p.State ?? "").Contains("fail", StringComparison.OrdinalIgnoreCase))
                dev.Faults.Add(new HardwareFault($"psu:{n}", FaultSeverity.Critical, $"PSU {n}",
                    $"{p.Name}: {p.State} ({p.Model?.Trim()}) — no power redundancy while it is down"));
        }

        // RAID controllers — the write-cache battery lives here, not in the Battery subsystem, and it
        // shows on the controller's RollupStatus while its own Status stays OK (verified: Atlas H710
        // and Sagan H710P both read Status 1000 / RollupStatus 3000).
        var ctrls = await GetAsync<OmeInventory<OmeRaidController>>($"api/DeviceService/Devices({d.Id})/InventoryDetails('serverRaidControllers')", ct);
        foreach (var c in ctrls?.InventoryInfo ?? [])
        {
            var st = MapStatus(c.RollupStatus);
            if (st is not (HardwareStatus.Warning or HardwareStatus.Critical)) continue;
            var comp = (c.Name ?? "RAID controller").Trim();
            dev.Faults.Add(new HardwareFault($"raid-controller:{Slug(comp)}", st == HardwareStatus.Critical ? FaultSeverity.Critical : FaultSeverity.Warning, comp,
                $"{comp} rolls up {c.RollupStatusString ?? st.ToString()} — on this estate that has meant a degraded write-cache battery (write-through forced)"));
        }

        // A subsystem that is Warning/Critical with nothing specific above to explain it still deserves a line.
        foreach (var (name, st) in dev.Subsystems)
        {
            if (st is not (HardwareStatus.Warning or HardwareStatus.Critical)) continue;
            var explained = name switch
            {
                "Storage" => dev.Faults.Any(f => f.Key.StartsWith("disk-") || f.Key.StartsWith("raid-")),
                "PSU" or "PowerSupply" => dev.Faults.Any(f => f.Key.StartsWith("psu:")),
                _ => false,
            };
            if (!explained)
                dev.Faults.Add(new HardwareFault($"subsystem:{Slug(name)}", st == HardwareStatus.Critical ? FaultSeverity.Critical : FaultSeverity.Warning,
                    name, $"{name} subsystem is {st}"));
        }

        return dev;
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await EnsureTokenAsync(force: attempt > 0, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, path.Replace(" ", "%20"));
            req.Headers.Add("X-Auth-Token", token);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        return default;
    }

    private async Task<string> EnsureTokenAsync(bool force, CancellationToken ct)
    {
        if (_token != null && !force) return _token;
        await _authLock.WaitAsync(ct);
        try
        {
            if (_token != null && !force) return _token;
            // Not PostAsJsonAsync: System.Net.Http.Json defaults to the Web options, which camel-case
            // the body to userName/password — and OME answers HTTP 400 to that. It wants the
            // property names exactly as Dell spells them, so serialise with no naming policy.
            var body = JsonSerializer.Serialize(new { UserName = _opt.User, Password = _opt.Password, SessionType = "API" });
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/SessionService/Sessions", content, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"OME sign-in failed: HTTP {(int)resp.StatusCode}");
            _token = resp.Headers.TryGetValues("X-Auth-Token", out var v) ? v.FirstOrDefault() : null;
            return _token ?? throw new HttpRequestException("OME sign-in returned no X-Auth-Token");
        }
        finally { _authLock.Release(); }
    }

    public void Dispose() => _http.Dispose();

    // ── Mapping helpers ──────────────────────────────────────────────────────

    internal static HardwareStatus MapStatus(JsonElement? el)
    {
        if (el is null) return HardwareStatus.Unknown;
        var e = el.Value;
        var code = e.ValueKind switch
        {
            JsonValueKind.Number => e.GetInt32(),
            JsonValueKind.String => int.TryParse(e.GetString(), out var n) ? n : -1,
            _ => -1,
        };
        return code switch { 1000 => HardwareStatus.Ok, 3000 => HardwareStatus.Warning, 4000 => HardwareStatus.Critical, _ => HardwareStatus.Unknown };
    }

    private static string BayFromName(string? name)
    {
        // "Disk 5 in Backplane 1 of RAID Controller in Slot 7" → "5";  "Power Supply 1" → "1"
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            if (int.TryParse(parts[i], out var n)) return n.ToString();
        return "?";
    }

    private static string FormatSize(string? gb)
    {
        if (!double.TryParse(gb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g)) return "";
        return g >= 1000 ? $"{g / 1024:0.#} TB" : $"{g:0} GB";
    }

    private static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}

// ── OME REST shapes (only the fields read) ────────────────────────────────────

internal record OmeList<T>([property: JsonPropertyName("value")] List<T>? Value);
internal record OmeInventory<T>([property: JsonPropertyName("InventoryInfo")] List<T>? InventoryInfo);

internal record OmeDevice(
    [property: JsonPropertyName("Id")] long Id,
    [property: JsonPropertyName("Type")] int Type,
    [property: JsonPropertyName("DeviceName")] string? DeviceName,
    [property: JsonPropertyName("DeviceServiceTag")] string? DeviceServiceTag,
    [property: JsonPropertyName("Model")] string? Model,
    [property: JsonPropertyName("Status")] JsonElement? Status,
    [property: JsonPropertyName("ConnectionState")] bool ConnectionState,
    [property: JsonPropertyName("DeviceManagement")] List<OmeManagement>? DeviceManagement);

internal record OmeManagement([property: JsonPropertyName("NetworkAddress")] string? NetworkAddress);

internal record OmeSubsystem(
    [property: JsonPropertyName("SubSystem")] string? SubSystem,
    [property: JsonPropertyName("RollupStatus")] JsonElement? RollupStatus);

internal record OmeDisk(
    [property: JsonPropertyName("DiskNumber")] string? DiskNumber,
    [property: JsonPropertyName("SlotNumber")] int? SlotNumber,
    [property: JsonPropertyName("ModelNumber")] string? ModelNumber,
    [property: JsonPropertyName("Status")] JsonElement? Status,
    [property: JsonPropertyName("StatusString")] string? StatusString,
    [property: JsonPropertyName("RaidStatus")] string? RaidStatus,
    [property: JsonPropertyName("PredictiveFailureState")] string? PredictiveFailureState,
    [property: JsonPropertyName("Size")] string? Size);

internal record OmePsu(
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("Status")] JsonElement? Status,
    [property: JsonPropertyName("State")] string? State,
    [property: JsonPropertyName("Model")] string? Model);

internal record OmeRaidController(
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("Status")] JsonElement? Status,
    [property: JsonPropertyName("RollupStatus")] JsonElement? RollupStatus,
    [property: JsonPropertyName("RollupStatusString")] string? RollupStatusString);
