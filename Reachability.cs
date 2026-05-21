using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace HyperInventory;

public static class ReachabilityChecker
{
    // Returns true if ICMP ping responds within timeout.
    public static async Task<bool> PingAsync(string address, TimeSpan timeout, CancellationToken ct = default)
    {
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
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    // Returns true if TCP port is open within timeout.
    public static async Task<bool> TestTcpPortAsync(string address, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await tcp.ConnectAsync(address, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    // Tests WinRM auth via PowerShell Negotiate auth. Returns AuthState + optional error.
    public static async Task<(AuthState State, string? Error)> TestWinRmAuthAsync(
        string address, Credentials creds, int port, TimeSpan timeout, CancellationToken ct = default)
    {
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
            if (result.StartsWith("AUTH_OK")) return (AuthState.Ok, null);
            var msg = result.StartsWith("AUTH_FAIL:") ? result[10..] : result;
            return (AuthState.Failed, msg.Trim());
        }
        catch (OperationCanceledException)
        {
            return (AuthState.Failed, "Timeout");
        }
        catch (Exception ex)
        {
            return (AuthState.Failed, ex.Message);
        }
    }

    // Runs a PowerShell script via stdin and returns stdout.
    public static async Task<string> RunPowerShellAsync(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var psi = new ProcessStartInfo("powershell.exe", "-NonInteractive -NoProfile -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        await proc.StandardInput.WriteAsync(script.AsMemory(), cts.Token);
        proc.StandardInput.Close();

        var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        return stdout;
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
