using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace HyperInventory;

public static class ReachabilityChecker
{
    // Returns true if ICMP ping responds within timeout.
    public static async Task<bool> PingAsync(string address, TimeSpan timeout, CancellationToken ct = default)
    {
        DevLog.Step($"[ICMP] pinging {address}…");
        try
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var psi = new ProcessStartInfo("ping.exe", $"-n 1 -w {(int)timeout.TotalMilliseconds} {address}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(cts.Token);
            var ok = proc.ExitCode == 0;
            if (ok) DevLog.Ok($"[ICMP] {address} reachable"); else DevLog.Warn($"[ICMP] {address} no response");
            return ok;
        }
        catch (Exception ex) { DevLog.Err($"[ICMP] exception: {ex.Message}"); return false; }
    }

    // Returns true if TCP port is open within timeout.
    public static async Task<bool> TestTcpPortAsync(string address, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        DevLog.Step($"[TCP]  testing {address}:{port}…");
        try
        {
            using var tcp = new TcpClient();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await tcp.ConnectAsync(address, port, cts.Token);
            DevLog.Ok($"[TCP]  {address}:{port} open");
            return true;
        }
        catch (Exception ex) { DevLog.Warn($"[TCP]  {address}:{port} closed/timeout — {ex.Message}"); return false; }
    }

    // Ensures the local WinRM service is running (needed for WSMan:\ provider and outbound auth).
    // Requires admin. Safe to call repeatedly — no-ops if already running.
    public static async Task EnsureWinRmServiceAsync(CancellationToken ct = default)
    {
        DevLog.Step("[WINRM] checking local WinRM service…");
        const string script =
            "$svc = Get-Service WinRM -ErrorAction SilentlyContinue\r\n" +
            "if (-not $svc) { Write-Output 'NOT_FOUND'; exit }\r\n" +
            "if ($svc.Status -eq 'Running') { Write-Output 'RUNNING'; exit }\r\n" +
            "Set-Service WinRM -StartupType Automatic -ErrorAction Stop\r\n" +
            "Start-Service WinRM -ErrorAction Stop\r\n" +
            "Write-Output 'STARTED'";
        try
        {
            var result = await RunPowerShellAsync(script, TimeSpan.FromSeconds(15), ct);
            if (result.Contains("STARTED"))       DevLog.Ok("[WINRM] WinRM service started");
            else if (result.Contains("RUNNING"))  DevLog.Ok("[WINRM] WinRM service already running");
            else if (result.Contains("NOT_FOUND")) DevLog.Warn("[WINRM] WinRM service not found on this machine");
            else DevLog.Warn($"[WINRM] unexpected: {result.Trim()}");
        }
        catch (Exception ex) { DevLog.Err($"[WINRM] failed to start service (not admin?): {ex.Message}"); }
    }

    // Ensures the address is in the local WinRM TrustedHosts list. Requires admin.
    public static async Task EnsureTrustedHostAsync(string address, CancellationToken ct = default)
    {
        DevLog.Step($"[TRUST] ensuring '{address}' is in TrustedHosts");
        var script = string.Format(
            "$cur = (Get-Item WSMan:\\localhost\\Client\\TrustedHosts -ErrorAction SilentlyContinue).Value\r\n" +
            "if ($cur -notlike '*{0}*') {{\r\n" +
            "  Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value '{0}' -Concatenate -Force\r\n" +
            "  Write-Output 'ADDED'\r\n" +
            "}} else {{\r\n" +
            "  Write-Output 'ALREADY'\r\n" +
            "}}",
            EscapePs(address));
        try
        {
            var result = await RunPowerShellAsync(script, TimeSpan.FromSeconds(10), ct);
            if (result.Contains("ADDED")) DevLog.Ok($"[TRUST] added '{address}' to TrustedHosts");
            else if (result.Contains("ALREADY")) DevLog.Ok($"[TRUST] '{address}' already in TrustedHosts");
            else DevLog.Warn($"[TRUST] unexpected result: {result.Trim()}");
        }
        catch (Exception ex)
        {
            DevLog.Err($"[TRUST] failed (not admin?): {ex.Message}");
        }
    }

    // Tests WinRM auth via PowerShell Negotiate auth. Returns AuthState + optional error.
    public static async Task<(AuthState State, string? Error)> TestWinRmAuthAsync(
        string address, Credentials creds, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        DevLog.Step($"[AUTH] testing WinRM auth → {address}:{port} as '{creds.Username}'");

        // Build script using string.Format to avoid raw-string/PowerShell brace conflicts.
        var script = string.Format(
            "$ErrorActionPreference = 'Stop'\r\n" +
            "try {{\r\n" +
            "  $pw = ConvertTo-SecureString '{0}' -AsPlainText -Force\r\n" +
            "  $cred = New-Object System.Management.Automation.PSCredential('{1}', $pw)\r\n" +
            "  Invoke-Command -ComputerName '{2}' -Credential $cred -Authentication Negotiate" +
            " -Port {3} -ScriptBlock {{ 'ok' }} -ErrorAction Stop | Out-Null\r\n" +
            "  Write-Output 'AUTH_OK'\r\n" +
            "}} catch {{\r\n" +
            "  $m = $_.Exception.Message\r\n" +
            "  Write-Output \"AUTH_FAIL:$m\"\r\n" +
            "}}",
            EscapePs(creds.GetPassword()), EscapePs(creds.Username), address, port);

        try
        {
            var result = await RunPowerShellAsync(script, timeout, ct);
            DevLog.Step($"[AUTH] raw result: {result.Trim()}");

            if (result.Contains("TRUSTED_FAIL:"))
                DevLog.Warn($"[AUTH] TrustedHosts update failed (not admin?): {result}");

            if (result.Contains("AUTH_OK"))
            {
                DevLog.Ok($"[AUTH] success for {address}");
                return (AuthState.Ok, null);
            }

            var failIdx = result.IndexOf("AUTH_FAIL:", StringComparison.Ordinal);
            var msg = failIdx >= 0 ? result[(failIdx + 10)..] : result;
            msg = msg.Trim();
            DevLog.Err($"[AUTH] failed for {address}: {msg}");
            return (AuthState.Failed, msg);
        }
        catch (OperationCanceledException)
        {
            DevLog.Err($"[AUTH] timeout after {timeout.TotalSeconds}s for {address}");
            return (AuthState.Failed, "Timeout");
        }
        catch (Exception ex)
        {
            DevLog.Err($"[AUTH] exception for {address}: {ex}");
            return (AuthState.Failed, ex.Message);
        }
    }

    // Runs a PowerShell script via a temp file and returns stdout.
    // Using -File instead of -Command - avoids a PowerShell 5.1 bug where WinRM errors
    // silently swallow all output when the script is piped via stdin.
    public static async Task<string> RunPowerShellAsync(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        DevLog.Script(script);

        var tmp = Path.Combine(Path.GetTempPath(), $"vmentory_{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(tmp, script, System.Text.Encoding.UTF8, cts.Token);

            var psi = new ProcessStartInfo(
                "powershell.exe",
                $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;

            // Read stdout and stderr concurrently to avoid deadlock if either pipe fills.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            await proc.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            DevLog.PsOut(stdout);
            DevLog.PsErr(stderr);
            DevLog.ExitCode(proc.ExitCode);

            return stdout;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // Resolve hostname DNS at add-time.
    public static async Task<(bool Resolved, string Fqdn, string Error)> ResolveFqdnAsync(string address)
    {
        try
        {
            var entry = await System.Net.Dns.GetHostEntryAsync(address);
            return (true, entry.HostName, "");
        }
        catch (Exception ex)
        {
            return (false, address, ex.Message);
        }
    }

    // Escape a string for safe embedding in single-quoted PowerShell context.
    public static string EscapePs(string s) => s.Replace("'", "''");
}
