using System.Text.Json.Serialization;

namespace HyperInventory;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthState { Unknown, Ok, Failed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanState { Idle, Scanning, Done, Error }

public class Reachability
{
    public bool Icmp { get; set; }
    public bool WinRm { get; set; }
    public AuthState Auth { get; set; } = AuthState.Unknown;
    public DateTimeOffset CheckedAt { get; set; }
    public string? ErrorDetail { get; set; }
}

public class Volume
{
    public string DriveLetter { get; set; } = "";
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
}

public class Vhd
{
    public string Path { get; set; } = "";
    public double ThickGb { get; set; }
    public double ActualGb { get; set; }
}

public class Vm
{
    public string HostId { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public int Generation { get; set; }
    public int VCpuCount { get; set; }
    public long StartupRamMb { get; set; }
    public long AssignedRamMb { get; set; }
    public bool DynamicMemory { get; set; }
    public List<Vhd> Vhds { get; set; } = [];
    public int NicCount { get; set; }
    public string Uptime { get; set; } = "";
    public string IntegrationServices { get; set; } = "";

    [JsonIgnore]
    public double TotalThickGb => Vhds.Sum(v => v.ThickGb);
    [JsonIgnore]
    public double TotalActualGb => Vhds.Sum(v => v.ActualGb);
}

public class Host
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Address { get; set; } = "";
    public string Fqdn { get; set; } = "";
    public string OsCaption { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string LastBoot { get; set; } = "";
    public string CpuModel { get; set; } = "";
    public int SocketCount { get; set; }
    public int TotalCores { get; set; }
    public int TotalLogicalProcs { get; set; }
    public double TotalRamGb { get; set; }
    public List<Volume> Volumes { get; set; } = [];
    public List<Vm> Vms { get; set; } = [];
    public Reachability Reachability { get; set; } = new();
    public ScanState ScanState { get; set; } = ScanState.Idle;
    public string ScanError { get; set; } = "";
    public DateTimeOffset? LastScanned { get; set; }
    public bool UseGlobalCreds { get; set; } = true;
    public string AddError { get; set; } = "";
    public bool Connecting { get; set; }

    [JsonIgnore]
    public Credentials? PerHostCreds { get; set; }
}

public class ClusterTotals
{
    public int HostCount { get; set; }
    public int TotalCores { get; set; }
    public double TotalRamGb { get; set; }
    public int TotalVCpu { get; set; }
    public double TotalVmRamGb { get; set; }
    public double RamCommitRatio { get; set; }
    public double CpuCommitRatio { get; set; }
}

public class DiffEntry
{
    public string HostId { get; set; } = "";
    public string VmName { get; set; } = "";
    public string? Field { get; set; }
    public string? OldVal { get; set; }
    public string? NewVal { get; set; }
}

public class SessionDiff
{
    public List<DiffEntry> Added { get; set; } = [];
    public List<DiffEntry> Removed { get; set; } = [];
    public List<DiffEntry> Changed { get; set; } = [];
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0;
}

public class SseEvent
{
    public string Type { get; set; } = "";
    public object? Data { get; set; }
}

// Credentials: password held as byte[] and zeroed on clear
public class Credentials : IDisposable
{
    public string Username { get; private set; }
    private byte[] _password;
    private bool _disposed;

    public Credentials(string username, string password)
    {
        Username = username;
        _password = System.Text.Encoding.UTF8.GetBytes(password);
    }

    public string GetPassword()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return System.Text.Encoding.UTF8.GetString(_password);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Array.Clear(_password, 0, _password.Length);
            _password = [];
            Username = "";
            _disposed = true;
        }
    }
}
