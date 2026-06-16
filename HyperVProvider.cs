using VMentory.Core;

namespace VMentory.Web;

// Hyper-V provider: today it wraps the WinRM-over-PowerShell Scanner/ReachabilityChecker; in the
// agent slice this transport is replaced by the gRPC/mTLS agent client (ARCHITECTURE.md), at which
// point it likely relocates to VMentory.Providers.HyperV. Resolves its own creds + port from the
// Store/AppConfig so the IVirtualizationProvider contract stays credential-free.
public sealed class HyperVProvider(Store store, AppConfig config) : IVirtualizationProvider
{
    public PlatformKind Platform => PlatformKind.HyperV;

    // Phase-1 reality: read-only inventory + live stats. The management/migration verb flags exist
    // (ProviderCapability) but are deliberately NOT advertised yet — 2.2 flips on Start|Stop|
    // Reconfigure (ENG-0007 light management) without reworking this model.
    public ProviderCapabilities Capabilities { get; } =
        new(ProviderCapability.Inventory | ProviderCapability.LiveStats);

    public Task<(bool Ok, string Error)> QuickConnectAsync(Host host, CancellationToken ct = default)
    {
        var creds = store.GetEffectiveCreds(host);
        if (creds == null) return Task.FromResult((false, "No credentials"));
        return Scanner.QuickConnectAsync(host, creds, config.WinRmPort, ct);
    }

    public Task<(bool Ok, string Error)> ScanAsync(Host host, CancellationToken ct = default)
    {
        var creds = store.GetEffectiveCreds(host);
        if (creds == null) return Task.FromResult((false, "No credentials"));
        return Scanner.ScanHostAsync(host, creds, config.WinRmPort, ct);
    }
}
