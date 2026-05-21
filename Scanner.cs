using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyperInventory;

public static class Scanner
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Quick info pulled right after a host is added (before full scan).
    public static async Task<(bool Ok, string Error)> QuickConnectAsync(
        Host host, Credentials creds, int winrmPort, CancellationToken ct = default)
    {
        var script = BuildRemoteWrapper(host.Address, creds, winrmPort, QuickInfoScript);

        try
        {
            var raw = await ReachabilityChecker.RunPowerShellAsync(
                script, TimeSpan.FromSeconds(15), ct);

            var json = ExtractJson(raw);
            if (json == null) return (false, "No JSON in response");

            var node = JsonNode.Parse(json);
            if (node == null) return (false, "Invalid JSON");

            host.Fqdn = node["FQDN"]?.GetValue<string>() ?? host.Address;
            host.OsCaption = node["OSCaption"]?.GetValue<string>() ?? "";
            host.OsVersion = node["OSVersion"]?.GetValue<string>() ?? "";
            host.Manufacturer = node["Manufacturer"]?.GetValue<string>() ?? "";
            host.Model = node["Model"]?.GetValue<string>() ?? "";

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // Full inventory scan for a single host.
    public static async Task<(bool Ok, string Error)> ScanHostAsync(
        Host host, Credentials creds, int winrmPort, CancellationToken ct = default)
    {
        var script = BuildRemoteWrapper(host.Address, creds, winrmPort, FullInventoryScript);

        try
        {
            var raw = await ReachabilityChecker.RunPowerShellAsync(
                script, TimeSpan.FromSeconds(60), ct);

            var json = ExtractJson(raw);
            if (json == null)
                return (false, string.IsNullOrWhiteSpace(raw) ? "No output from host" : $"Unexpected output: {raw[..Math.Min(200, raw.Length)]}");

            var node = JsonNode.Parse(json);
            if (node == null) return (false, "Could not parse response JSON");

            if (node["Error"] is JsonNode errNode)
                return (false, errNode.GetValue<string>());

            MapInventory(host, node);
            return (true, "");
        }
        catch (OperationCanceledException)
        {
            return (false, "Scan timed out (60s)");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void MapInventory(Host host, JsonNode node)
    {
        host.Fqdn = node["FQDN"]?.GetValue<string>() ?? host.Address;
        host.OsCaption = node["OSCaption"]?.GetValue<string>() ?? "";
        host.OsVersion = node["OSVersion"]?.GetValue<string>() ?? "";
        host.LastBoot = node["LastBoot"]?.GetValue<string>() ?? "";
        host.Manufacturer = node["Manufacturer"]?.GetValue<string>() ?? "";
        host.Model = node["Model"]?.GetValue<string>() ?? "";
        host.Serial = node["Serial"]?.GetValue<string>() ?? "";
        host.CpuModel = node["CPUModel"]?.GetValue<string>() ?? "";
        host.SocketCount = node["SocketCount"]?.GetValue<int>() ?? 0;
        host.TotalCores = node["TotalCores"]?.GetValue<int>() ?? 0;
        host.TotalLogicalProcs = node["TotalLogicalProcs"]?.GetValue<int>() ?? 0;
        host.TotalRamGb = Math.Round((node["TotalRAMBytes"]?.GetValue<long>() ?? 0) / 1_073_741_824.0, 1);

        host.Volumes = [];
        if (node["Volumes"] is JsonArray vols)
        {
            foreach (var v in vols)
            {
                if (v == null) continue;
                host.Volumes.Add(new Volume
                {
                    DriveLetter = v["DriveLetter"]?.GetValue<string>() ?? "",
                    TotalGb = Math.Round((v["TotalBytes"]?.GetValue<long>() ?? 0) / 1_073_741_824.0, 1),
                    FreeGb = Math.Round((v["FreeBytes"]?.GetValue<long>() ?? 0) / 1_073_741_824.0, 1),
                });
            }
        }

        host.Vms = [];
        if (node["VMs"] is JsonArray vms)
        {
            foreach (var v in vms)
            {
                if (v == null) continue;
                var vm = new Vm
                {
                    HostId = host.Id,
                    Name = v["Name"]?.GetValue<string>() ?? "",
                    State = v["State"]?.GetValue<string>() ?? "",
                    Generation = v["Generation"]?.GetValue<int>() ?? 0,
                    VCpuCount = v["VCPUCount"]?.GetValue<int>() ?? 0,
                    StartupRamMb = (v["StartupRAMBytes"]?.GetValue<long>() ?? 0) / 1_048_576,
                    AssignedRamMb = (v["AssignedRAMBytes"]?.GetValue<long>() ?? 0) / 1_048_576,
                    DynamicMemory = v["DynamicMemory"]?.GetValue<bool>() ?? false,
                    Uptime = v["Uptime"]?.GetValue<string>() ?? "",
                    IntegrationServices = v["IntegrationServices"]?.GetValue<string>() ?? "",
                    NicCount = v["NICCount"]?.GetValue<int>() ?? 0,
                };

                if (v["VHDs"] is JsonArray vhds)
                {
                    foreach (var d in vhds)
                    {
                        if (d == null) continue;
                        vm.Vhds.Add(new Vhd
                        {
                            Path = d["Path"]?.GetValue<string>() ?? "",
                            ThickGb = Math.Round((d["ThickBytes"]?.GetValue<long>() ?? 0) / 1_073_741_824.0, 1),
                            ActualGb = Math.Round((d["ActualBytes"]?.GetValue<long>() ?? 0) / 1_073_741_824.0, 1),
                        });
                    }
                }

                host.Vms.Add(vm);
            }
        }
    }

    // Extract the last JSON object from PowerShell output (may contain other lines).
    private static string? ExtractJson(string raw)
    {
        for (int i = raw.Length - 1; i >= 0; i--)
        {
            if (raw[i] == '{' || raw[i] == '[')
            {
                var end = raw.LastIndexOf(raw[i] == '{' ? '}' : ']');
                if (end > i) return raw[i..(end + 1)];
            }
        }
        return null;
    }

    private static string BuildRemoteWrapper(string address, Credentials creds, int port, string innerScript)
    {
        var escapedPw = ReachabilityChecker.EscapePs(creds.GetPassword());
        var escapedUser = ReachabilityChecker.EscapePs(creds.Username);

        // Use string.Format to avoid C# raw-string conflicts with PowerShell braces.
        return string.Format(
            "$ErrorActionPreference = 'Stop'\r\n" +
            "$pw = ConvertTo-SecureString '{0}' -AsPlainText -Force\r\n" +
            "$cred = New-Object System.Management.Automation.PSCredential('{1}', $pw)\r\n" +
            "try {{\r\n" +
            "    Invoke-Command -ComputerName '{2}' -Credential $cred -Authentication Negotiate" +
            " -Port {3} -ErrorAction Stop -ScriptBlock {{\r\n" +
            "        {4}\r\n" +
            "    }}\r\n" +
            "}} catch {{\r\n" +
            "    $m = $_.Exception.Message\r\n" +
            "    @{{ Error = $m }} | ConvertTo-Json -Compress\r\n" +
            "}}\r\n" +
            "$pw = $null; [GC]::Collect()",
            escapedPw, escapedUser, address, port, innerScript);
    }

    private const string QuickInfoScript = """
        $ErrorActionPreference = 'Stop'
        try {
            $os = Get-WmiObject Win32_OperatingSystem -ErrorAction Stop
            $cs = Get-WmiObject Win32_ComputerSystem -ErrorAction Stop
            @{
                FQDN        = [System.Net.Dns]::GetHostEntry([string]::Empty).HostName
                OSCaption   = $os.Caption
                OSVersion   = $os.Version
                Manufacturer = $cs.Manufacturer
                Model       = $cs.Model
            } | ConvertTo-Json -Compress
        } catch {
            @{ Error = $_.Exception.Message } | ConvertTo-Json -Compress
        }
        """;

    private const string FullInventoryScript = """
        $ErrorActionPreference = 'Stop'
        try {
            $os   = Get-WmiObject Win32_OperatingSystem  -ErrorAction Stop
            $cs   = Get-WmiObject Win32_ComputerSystem   -ErrorAction Stop
            $bios = Get-WmiObject Win32_BIOS             -ErrorAction Stop
            $cpus = @(Get-WmiObject Win32_Processor      -ErrorAction Stop)

            $result = @{
                FQDN             = [System.Net.Dns]::GetHostEntry([string]::Empty).HostName
                OSCaption        = $os.Caption
                OSVersion        = $os.Version
                LastBoot         = $os.ConvertToDateTime($os.LastBootUpTime).ToString('o')
                Manufacturer     = $cs.Manufacturer
                Model            = $cs.Model
                TotalRAMBytes    = [long]$cs.TotalPhysicalMemory
                Serial           = $bios.SerialNumber
                CPUModel         = $cpus[0].Name
                SocketCount      = $cpus.Count
                TotalCores       = [int]($cpus | Measure-Object -Property NumberOfCores -Sum).Sum
                TotalLogicalProcs = [int]($cpus | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
            }

            $result.Volumes = @(
                Get-WmiObject Win32_LogicalDisk -Filter "DriveType=3" | ForEach-Object {
                    @{ DriveLetter = $_.DeviceID; TotalBytes = [long]$_.Size; FreeBytes = [long]$_.FreeSpace }
                }
            )

            $hvMod = Get-Module -ListAvailable -Name Hyper-V -ErrorAction SilentlyContinue
            if ($hvMod) {
                $result.VMs = @(
                    Get-VM | ForEach-Object {
                        $vm = $_
                        $vhds = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object {
                            $hdrive = $_
                            try {
                                $v = Get-VHD -Path $hdrive.Path -ErrorAction Stop
                                @{ Path = $hdrive.Path; ThickBytes = [long]$v.Size; ActualBytes = [long]$v.FileSize }
                            } catch {
                                @{ Path = $hdrive.Path; ThickBytes = 0L; ActualBytes = 0L }
                            }
                        })
                        @{
                            Name              = $vm.Name
                            State             = $vm.State.ToString()
                            Generation        = [int]$vm.Generation
                            VCPUCount         = [int]$vm.ProcessorCount
                            StartupRAMBytes   = [long]$vm.MemoryStartup
                            AssignedRAMBytes  = [long]$vm.MemoryAssigned
                            DynamicMemory     = [bool]$vm.DynamicMemoryEnabled
                            Uptime            = if ($vm.Uptime.TotalSeconds -gt 0) { $vm.Uptime.ToString() } else { '' }
                            IntegrationServices = if ($vm.IntegrationServicesVersion) { $vm.IntegrationServicesVersion.ToString() } else { '' }
                            NICCount          = [int]@(Get-VMNetworkAdapter -VM $vm).Count
                            VHDs              = $vhds
                        }
                    }
                )
            } else {
                $result.VMs = @()
            }

            $result | ConvertTo-Json -Depth 10 -Compress
        } catch {
            @{ Error = $_.Exception.Message } | ConvertTo-Json -Compress
        }
        """;
}
