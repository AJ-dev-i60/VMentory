using System.Text.Json.Serialization;

namespace VMentory.Core;

// Which virtualization platform a host/provider belongs to. The platform-neutral discriminator
// that lets Observe (and later Migrate/Deploy) span Hyper-V and Proxmox on one model.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlatformKind
{
    HyperV,
    Proxmox,
}
