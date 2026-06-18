using VMentory.Core;

namespace VMentory.Web;

// Routes IVirtualizationProvider calls to the right implementation by platform.
// Registered as a singleton; both providers are also singletons.
public class ProviderRegistry(IEnumerable<IVirtualizationProvider> providers)
{
    private readonly Dictionary<PlatformKind, IVirtualizationProvider> _map =
        providers.ToDictionary(p => p.Platform);

    public IVirtualizationProvider For(PlatformKind platform) =>
        _map.TryGetValue(platform, out var p) ? p
        : throw new InvalidOperationException($"No provider registered for platform '{platform}'");
}
