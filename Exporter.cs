using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace HyperInventory;

public static class Exporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static byte[] ToJson(IReadOnlyList<Host> hosts, ClusterTotals totals, SessionDiff diff)
    {
        var export = new
        {
            exportedAt = DateTimeOffset.UtcNow,
            totals,
            diff,
            hosts = hosts.Select(h => new
            {
                h.Id, h.Address, h.Fqdn, h.OsCaption, h.OsVersion,
                h.Manufacturer, h.Model, h.Serial, h.LastBoot,
                h.CpuModel, h.SocketCount, h.TotalCores, h.TotalLogicalProcs, h.TotalRamGb,
                h.Volumes, h.Vms,
                reachability = new { h.Reachability.Icmp, h.Reachability.WinRm, h.Reachability.Auth, h.Reachability.CheckedAt },
            })
        };
        return JsonSerializer.SerializeToUtf8Bytes(export, JsonOpts);
    }

    // Returns a zip with hosts.csv and vms.csv inside.
    public static byte[] ToCsvZip(IReadOnlyList<Host> hosts)
    {
        using var ms = new MemoryStream();
        using var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true);

        WriteEntry(zip, "hosts.csv", BuildHostsCsv(hosts));
        WriteEntry(zip, "vms.csv", BuildVmsCsv(hosts));

        zip.Dispose();
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string BuildHostsCsv(IReadOnlyList<Host> hosts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Address,FQDN,OS,Version,Manufacturer,Model,Serial,LastBoot," +
                      "CPUModel,Sockets,Cores,LogicalProcs,TotalRamGB," +
                      "ICMP,WinRM,Auth,VMCount,LastScanned");

        foreach (var h in hosts)
        {
            sb.AppendLine(string.Join(",",
                Q(h.Id), Q(h.Address), Q(h.Fqdn), Q(h.OsCaption), Q(h.OsVersion),
                Q(h.Manufacturer), Q(h.Model), Q(h.Serial), Q(h.LastBoot),
                Q(h.CpuModel), h.SocketCount, h.TotalCores, h.TotalLogicalProcs,
                h.TotalRamGb.ToString("F1"),
                h.Reachability.Icmp, h.Reachability.WinRm, h.Reachability.Auth,
                h.Vms.Count,
                Q(h.LastScanned?.ToString("o") ?? "")));
        }
        return sb.ToString();
    }

    private static string BuildVmsCsv(IReadOnlyList<Host> hosts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("HostId,HostAddress,VMName,State,Generation,vCPU," +
                      "StartupRamMB,AssignedRamMB,DynamicMemory," +
                      "VHDCount,TotalThickGB,TotalActualGB,NICCount,Uptime,IntegrationServices");

        foreach (var h in hosts)
        foreach (var v in h.Vms)
        {
            sb.AppendLine(string.Join(",",
                Q(h.Id), Q(h.Address), Q(v.Name), Q(v.State), v.Generation, v.VCpuCount,
                v.StartupRamMb, v.AssignedRamMb, v.DynamicMemory,
                v.Vhds.Count,
                v.TotalThickGb.ToString("F1"), v.TotalActualGb.ToString("F1"),
                v.NicCount, Q(v.Uptime), Q(v.IntegrationServices)));
        }
        return sb.ToString();
    }

    // Quote a CSV field.
    private static string Q(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
}
