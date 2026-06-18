using System.Collections.Concurrent;

namespace VMentory.Core.Secrets;

// In-memory ISecretStore — used when no VMENTORY_KEK is configured. Secrets are held for the
// lifetime of the process only; they are NOT persisted and are lost on restart. Registered as
// Singleton so the dictionary survives across request scopes.
public sealed class EphemeralSecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _store = new();

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult<string?>(_store.TryGetValue(key, out var v) ? v : null);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
