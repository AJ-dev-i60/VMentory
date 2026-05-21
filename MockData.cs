namespace HyperInventory;

// Generates realistic fake data for --mock mode (no real WinRM calls).
public static class MockData
{
    public static List<Host> Generate()
    {
        return
        [
            MakeHost("hv-prod-01", "hv-prod-01.corp.local", "Windows Server 2022 Datacenter",
                cores: 32, ramGb: 512, auth: AuthState.Ok,
                vms:
                [
                    MakeVm("WEB-01", "Running", 4, 8192, vhdThick: 120, vhdActual: 45),
                    MakeVm("WEB-02", "Running", 4, 8192, vhdThick: 120, vhdActual: 52),
                    MakeVm("DB-PROD", "Running", 8, 32768, vhdThick: 500, vhdActual: 310),
                    MakeVm("APP-01", "Running", 4, 16384, vhdThick: 200, vhdActual: 88),
                    MakeVm("MGMT-DC", "Running", 2, 4096, vhdThick: 80, vhdActual: 30),
                    MakeVm("BACKUP-SRV", "Saved", 2, 4096, vhdThick: 1000, vhdActual: 420),
                ]),

            MakeHost("hv-prod-02", "hv-prod-02.corp.local", "Windows Server 2022 Datacenter",
                cores: 32, ramGb: 384, auth: AuthState.Ok,
                vms:
                [
                    MakeVm("WEB-03", "Running", 4, 8192, vhdThick: 120, vhdActual: 48),
                    MakeVm("API-SRV", "Running", 6, 12288, vhdThick: 160, vhdActual: 72),
                    MakeVm("CACHE-01", "Running", 2, 8192, vhdThick: 60, vhdActual: 18),
                    MakeVm("DEV-BUILD", "Off", 4, 16384, vhdThick: 250, vhdActual: 95),
                ]),

            MakeHost("hv-dev-01", "hv-dev-01.corp.local", "Windows Server 2019 Standard",
                cores: 16, ramGb: 128, auth: AuthState.Ok, icmp: true, winrm: true,
                vms:
                [
                    MakeVm("DEV-WEB", "Running", 2, 4096, vhdThick: 80, vhdActual: 22),
                    MakeVm("DEV-DB", "Running", 2, 8192, vhdThick: 150, vhdActual: 65),
                    MakeVm("TEST-ENV", "Off", 2, 4096, vhdThick: 80, vhdActual: 30),
                ]),

            // Host that is reachable but auth fails
            MakeHost("hv-dr-01", "hv-dr-01.corp.local", "Windows Server 2022 Datacenter",
                cores: 24, ramGb: 256, auth: AuthState.Failed, icmp: true, winrm: true,
                vms: []),

            // Host that is completely unreachable
            MakeHost("hv-old-01", "hv-old-01.corp.local", "Windows Server 2016",
                cores: 0, ramGb: 0, auth: AuthState.Unknown, icmp: false, winrm: false,
                vms: []),
        ];
    }

    private static Host MakeHost(
        string addr, string fqdn, string os,
        int cores, double ramGb, AuthState auth,
        bool icmp = true, bool winrm = true,
        List<Vm>? vms = null)
    {
        var host = new Host
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Address = addr,
            Fqdn = fqdn,
            OsCaption = os,
            OsVersion = os.Contains("2022") ? "10.0.20348" : os.Contains("2019") ? "10.0.17763" : "10.0.14393",
            Manufacturer = "Dell Inc.",
            Model = "PowerEdge R740",
            Serial = $"SN{Random.Shared.Next(100000, 999999)}",
            LastBoot = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 90)).ToString("o"),
            CpuModel = "Intel(R) Xeon(R) Gold 6226R CPU @ 2.90GHz",
            SocketCount = cores > 0 ? 2 : 0,
            TotalCores = cores,
            TotalLogicalProcs = cores * 2,
            TotalRamGb = ramGb,
            ScanState = auth == AuthState.Failed || !icmp ? ScanState.Idle : ScanState.Done,
            LastScanned = auth == AuthState.Ok && icmp ? DateTimeOffset.UtcNow.AddMinutes(-3) : null,
            AddError = auth == AuthState.Failed ? "Authentication failed" : (!icmp ? "Host unreachable" : ""),
            Volumes =
            [
                new Volume { DriveLetter = "C:", TotalGb = 200, FreeGb = 85 },
                new Volume { DriveLetter = "D:", TotalGb = 2000, FreeGb = 800 },
            ],
            Reachability = new Reachability
            {
                Icmp = icmp,
                WinRm = winrm,
                Auth = auth,
                CheckedAt = DateTimeOffset.UtcNow,
                ErrorDetail = auth == AuthState.Failed ? "Access denied" : null,
            }
        };

        if (vms != null)
        {
            foreach (var vm in vms) vm.HostId = host.Id;
            host.Vms = vms;
        }

        return host;
    }

    private static Vm MakeVm(string name, string state, int vcpu, long ramMb, double vhdThick, double vhdActual) => new()
    {
        Name = name,
        State = state,
        Generation = 2,
        VCpuCount = vcpu,
        StartupRamMb = ramMb,
        AssignedRamMb = state == "Running" ? ramMb : 0,
        DynamicMemory = Random.Shared.Next(2) == 0,
        NicCount = 1,
        Uptime = state == "Running" ? $"{Random.Shared.Next(0, 30)}.{Random.Shared.Next(0, 23)}:{Random.Shared.Next(0, 59)}:{Random.Shared.Next(0, 59)}" : "",
        IntegrationServices = "6.0.9600.19727",
        Vhds =
        [
            new Vhd
            {
                Path = $@"D:\Hyper-V\{name}\{name}-disk0.vhdx",
                ThickGb = vhdThick,
                ActualGb = vhdActual,
            }
        ]
    };
}
