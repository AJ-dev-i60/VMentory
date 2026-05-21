using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;

namespace HyperInventory;

public static class Updater
{
    private const string Owner     = "AJ-dev-i60";
    private const string Repo      = "VMentory";
    private const string AssetName = "VMentory.exe";
    private const string ExeName   = "VMentory.exe";

    public static string CurrentVersion
    {
        get
        {
            var v = typeof(Updater).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    // Must be called before WebApplication.CreateBuilder.
    // Detects VMentory-update.exe beside the running exe and applies it via
    // rename-then-reexec (Windows cannot overwrite a running exe in-place).
    public static void ApplyPendingUpdate(ErrorLogger log)
    {
        var exePath = Environment.ProcessPath;
        if (exePath == null) return;

        // Skip in dev mode (dotnet run) — exe won't be named VMentory.exe
        if (!Path.GetFileName(exePath).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            return;

        var dir        = Path.GetDirectoryName(exePath)!;
        var updatePath = Path.Combine(dir, "VMentory-update.exe");
        if (!File.Exists(updatePath)) return;

        var bakPath = Path.Combine(dir, "VMentory.bak.exe");

        try
        {
            Console.WriteLine("  Update found — applying...");

            if (File.Exists(bakPath)) File.Delete(bakPath);

            // Rename running exe out of the way (frees the path, not the handle)
            File.Move(exePath, bakPath);

            // Place update in the canonical exe path
            File.Move(updatePath, exePath);

            // Re-exec the new binary with the same arguments
            var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
            foreach (var a in Environment.GetCommandLineArgs().Skip(1))
                psi.ArgumentList.Add(a);

            Process.Start(psi);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            log.LogError("Failed to apply update", ex);
            Console.WriteLine($"  Update apply failed: {ex.Message} — continuing with current version.");

            // Restore the original if we already moved it
            try
            {
                if (!File.Exists(exePath) && File.Exists(bakPath))
                    File.Move(bakPath, exePath);
            }
            catch { /* best-effort */ }
        }
    }

    // Fire-and-forget — call after app.StartAsync() so startup is not delayed.
    public static void StartBackgroundCheck(AppConfig cfg, ErrorLogger log)
    {
        if (cfg.NoUpdate) return;

        var exePath = Environment.ProcessPath;
        if (exePath == null || !Path.GetFileName(exePath).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            return; // skip in dev / test environments

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(12)); // let the app finish starting
            try { await CheckAndDownloadAsync(exePath, log); }
            catch (Exception ex) { log.LogError("Update check failed", ex); }
        });
    }

    private static async Task CheckAndDownloadAsync(string exePath, ErrorLogger log)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("VMentory", CurrentVersion));
        http.Timeout = TimeSpan.FromSeconds(30);

        var apiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        var json   = await http.GetStringAsync(apiUrl);
        var node   = JsonNode.Parse(json);
        if (node == null) return;

        var tagName   = node["tag_name"]?.GetValue<string>() ?? "";
        var remoteRaw = tagName.TrimStart('v');

        if (!System.Version.TryParse(remoteRaw, out var remote) ||
            !System.Version.TryParse(CurrentVersion, out var current))
            return;

        if (remote <= current) return;

        // Find the asset download URL
        string? downloadUrl = null;
        if (node["assets"] is JsonArray assets)
        {
            foreach (var asset in assets)
            {
                if (asset?["name"]?.GetValue<string>() == AssetName)
                {
                    downloadUrl = asset["browser_download_url"]?.GetValue<string>();
                    break;
                }
            }
        }
        if (downloadUrl == null) return;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n  Update available: v{remote} (current: v{current}) — downloading...");
        Console.ResetColor();

        var dir        = Path.GetDirectoryName(exePath)!;
        var tmpPath    = Path.Combine(dir, "VMentory-update.tmp");
        var updatePath = Path.Combine(dir, "VMentory-update.exe");

        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var netStream = await response.Content.ReadAsStreamAsync())
        await using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await netStream.CopyToAsync(fileStream);
        }

        File.Move(tmpPath, updatePath, overwrite: true);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Update ready (v{remote}). Restart VMentory to apply.");
        Console.ResetColor();
    }
}
