using System.Text.Json.Serialization;

namespace VMentory.Core.Fleet;

// ── ENG-0016: Proxmox fleet readings ──────────────────────────────────────────
// One reading per node per poll, exactly as the node reported it. The rule that shapes every type
// here: a number the node did not give us is null, never zero and never a default. A null renders
// as a blank with the reason; a zero renders as a zero. Unreachable nodes keep their last good
// reading, which the API serves with its age instead of pretending it is current.

public sealed class NodeReading
{
    public string HostId { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Node { get; set; }                  // PVE node name, e.g. "sirius16"
    public DateTimeOffset TakenAt { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = [];   // partial-read problems; the reading still stands

    public string? PveVersion { get; set; }
    public string? CpuModel { get; set; }
    public int? Sockets { get; set; }
    public int? Cores { get; set; }                    // physical cores, all sockets
    public int? Threads { get; set; }                  // logical CPUs
    public long? MemTotal { get; set; }
    public long? MemUsed { get; set; }
    public long? MemAvailable { get; set; }            // the kernel's MemAvailable
    public long? ArcSize { get; set; }                 // current ZFS ARC, from the node's RRD
    public double? Load1 { get; set; }
    public double? Load5 { get; set; }
    public double? Load15 { get; set; }
    public long? UptimeSeconds { get; set; }

    public List<GuestReading> Guests { get; set; } = [];
    public List<StorageReading> Storages { get; set; } = [];
    public List<ZpoolReading> Zpools { get; set; } = [];
    public List<string> Bridges { get; set; } = [];
}

// What the forced-command SSH probe returns (ENG-0016 §probe). Read far less often than the REST
// poll because neither value moves between reboots; carries its own timestamp.
public sealed class ProbeReading
{
    public DateTimeOffset TakenAt { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public long? ArcMax { get; set; }                  // effective ARC cap (arcstats c_max), bytes
    public string? Serial { get; set; }                // DMI product_serial = Dell service tag
    public string? Product { get; set; }
    // allocated bytes per volume, keyed "zfs:<dataset>", "lvm:<vg>/<lv>", "file:<absolute path>"
    [JsonIgnore] public Dictionary<string, long> Allocated { get; set; } = new(StringComparer.Ordinal);
    public int AllocatedEntries => Allocated.Count;
    // /etc/pve/storage.cfg facts per storage id: pool, vgname, path, sparse, thinpool, type
    [JsonIgnore] public Dictionary<string, Dictionary<string, string>> StorageCfg { get; set; } = new(StringComparer.Ordinal);
    // physical disks under each guest store, as the kernel sees them
    [JsonIgnore] public Dictionary<string, List<ProbeDisk>> Media { get; set; } = new(StringComparer.Ordinal);
}

public sealed record ProbeDisk(string Name, bool? Rotational, string Model, string Transport);

public sealed class GuestReading
{
    public int Vmid { get; set; }
    public string Type { get; set; } = "qemu";         // qemu | lxc
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";           // running | stopped | paused ...
    public string? Lock { get; set; }
    public bool Template { get; set; }
    public int? Vcpus { get; set; }                    // null when an LXC has no core limit
    public bool CpuUnlimited { get; set; }
    public long? MaxMem { get; set; }
    public long? Mem { get; set; }
    public long? UptimeSeconds { get; set; }
    public double? CpuBusy { get; set; }               // fraction of its own vCPUs in use, as PVE reports
    public string? OsType { get; set; }                // config ostype: win11, l26, ubuntu, debian ...
    public string? OsName { get; set; }                // guest agent pretty-name; null without an agent
    public bool AgentEnabled { get; set; }
    public List<string> Ips { get; set; } = [];        // live, from the guest agent / LXC interfaces; empty = unknown
    public string? IpSource { get; set; }              // "guest agent" | "container" | null
    public string? IpError { get; set; }               // why there are no IPs, when we know
    public string? Description { get; set; }
    public bool? Onboot { get; set; }                  // null only when the config could not be read
    public string? CpuType { get; set; }               // qemu "cpu:" type; null = not set in config
    public List<string> Tags { get; set; } = [];
    public List<GuestDisk> Disks { get; set; } = [];
    public List<string> Bridges { get; set; } = [];
    public bool HasIdeCloudInit { get; set; }
    public List<string> MigrationBlockers { get; set; } = [];   // passthrough, bind mounts, local ISO
    public string? ConfigError { get; set; }

    [JsonIgnore] public bool Running => Status == "running";
}

public sealed class GuestDisk
{
    public string Key { get; set; } = "";              // scsi0, rootfs, mp0, unused1 ...
    public string Storage { get; set; } = "";
    public string Volume { get; set; } = "";           // full volid "storage:vm-100-disk-0"
    public long? VirtualBytes { get; set; }
    public long? AllocatedBytes { get; set; }          // what is actually written — what a copy moves
    public DateTimeOffset? AllocatedReadAt { get; set; }
    public bool CloudInit { get; set; }
}

public sealed class StorageReading
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";             // zfspool | lvmthin | dir | ...
    public string Content { get; set; } = "";
    public bool Active { get; set; }
    public bool Enabled { get; set; }
    public bool Shared { get; set; }
    public long? Total { get; set; }
    public long? Used { get; set; }
    public long? Avail { get; set; }
    public string? ZfsPool { get; set; }               // the zpool behind a zfspool storage
    public string? ZfsPath { get; set; }               // its dataset, e.g. rpool/data
    public string? VgName { get; set; }                // lvm / lvmthin
    public string? Path { get; set; }                  // dir
    public bool? Sparse { get; set; }                  // zfspool: thin zvols? null = not read
    // Sum of the configured (virtual) sizes of the guest disks on this store, from guest configs.
    // The audit token cannot list a store's content, so orphaned volumes are not in this figure.
    public long? VirtualProvisioned { get; set; }
    public int? Volumes { get; set; }
    public int VirtualUnknown { get; set; }            // disks on this store whose size the config does not state
    public string? Media { get; set; }                 // ssd | nvme | hdd | mixed | unknown — null = not probed
    public string? MediaNote { get; set; }             // models / why unknown

    [JsonIgnore] public bool HoldsGuests => Content.Contains("images") || Content.Contains("rootdir");
}

public sealed class ZpoolReading
{
    public string Name { get; set; } = "";
    public long? Size { get; set; }
    public long? Alloc { get; set; }
    public long? Free { get; set; }
    public string? Health { get; set; }
    public List<VdevReading> Vdevs { get; set; } = [];
    public List<VdevReading> Spares { get; set; } = [];
    public bool LayoutRead { get; set; }
}

public sealed class VdevReading
{
    public string Name { get; set; } = "";             // mirror-0, raidz1-0, or a disk path
    public string? State { get; set; }
    public int Disks { get; set; }
}
