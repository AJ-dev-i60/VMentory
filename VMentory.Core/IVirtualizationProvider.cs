namespace VMentory.Core;

// The pivot from "Hyper-V tool" to "platform". Core talks to providers, never to platform-specific
// transport directly. Each provider declares its capabilities; callers honor them.
//
// Credential-free by design: a provider resolves its own connection details (WinRM creds for
// Hyper-V, an API token for Proxmox) rather than threading them through every call.
//
// Lifecycle and migration *verb methods* are deferred to 2.2/2.3 — the capability *flags* already
// exist (see ProviderCapability), so growing this single-consumer interface later is non-breaking.
public interface IVirtualizationProvider
{
    PlatformKind Platform { get; }
    ProviderCapabilities Capabilities { get; }

    // Quick info pulled right after a host is added (FQDN/OS/model), before a full scan.
    Task<(bool Ok, string Error)> QuickConnectAsync(Host host, CancellationToken ct = default);

    // Full inventory scan for a single host (hardware, volumes, VMs).
    Task<(bool Ok, string Error)> ScanAsync(Host host, CancellationToken ct = default);
}
