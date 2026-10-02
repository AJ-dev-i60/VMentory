using System.Globalization;
using System.Net;
using System.Text.Json;

namespace VMentory.Web.Fleet;

// Minimal PVE REST client for the fleet (ENG-0016). One instance = one node + one token. The
// read-only poller and the write verbs construct separate instances from separate tokens — the
// audit token is never handed to a write path (brief: "Do not reuse the audit token for writes").
public sealed class PveClient : IDisposable
{
    private readonly HttpClient _http;
    public string Address { get; }

    public PveClient(string address, string token, bool skipTls, TimeSpan? timeout = null)
    {
        Address = address;
        var handler = new HttpClientHandler();
        if (skipTls)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{address}:8006"),
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };
        // Gotcha #13: the PVEAPIToken value must bypass header validation.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"PVEAPIToken={token}");
    }

    public Task<JsonElement> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonElement> PostAsync(string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, form, ct);

    public Task<JsonElement> PutAsync(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, path, form, ct);

    public Task<JsonElement> DeleteAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, path, null, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, "/api2/json" + path);
        if (form != null) req.Content = new FormUrlEncodedContent(form);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new PveException(resp.StatusCode, Describe(resp, body), path);
        if (string.IsNullOrWhiteSpace(body)) return default;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    // PVE puts the useful part of an error in the reason phrase, or in {"errors":{param:msg}}.
    private static string Describe(HttpResponseMessage resp, string body)
    {
        var msg = resp.ReasonPhrase ?? resp.StatusCode.ToString();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
                msg += " — " + string.Join("; ", errs.EnumerateObject().Select(p => $"{p.Name}: {p.Value}"));
            else if (doc.RootElement.TryGetProperty("message", out var m))
                msg += " — " + m.GetString();
        }
        catch { }
        return $"HTTP {(int)resp.StatusCode}: {msg}";
    }

    public static string Enc(string s) => Uri.EscapeDataString(s);

    public void Dispose() => _http.Dispose();
}

public sealed class PveException(HttpStatusCode status, string message, string path) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string Path { get; } = path;
}

// PVE returns numbers as numbers in some endpoints and as strings in others (and "1"/1 for flags).
// Every read goes through these so a missing or unparsable field becomes null, never 0.
public static class Pj
{
    public static bool Has(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null,
        };
    }

    public static long? Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetInt64(out var l) ? l : (long)v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return (long)d;
        return null;
    }

    public static int? Int(JsonElement e, string name) => Long(e, name) is { } l ? (int)l : null;

    public static double? Dbl(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    public static bool Flag(JsonElement e, string name) => Str(e, name) is "1" or "true";

    public static IEnumerable<JsonElement> Arr(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    // "32G", "512M", "1T", "4194304" (bytes) → bytes. Config sizes use binary units.
    public static long? ParseSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var unit = char.ToUpperInvariant(s[^1]);
        var mult = unit switch { 'K' => 1L << 10, 'M' => 1L << 20, 'G' => 1L << 30, 'T' => 1L << 40, _ => 1L };
        var num = mult == 1 ? s : s[..^1];
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)(d * mult) : null;
    }
}
