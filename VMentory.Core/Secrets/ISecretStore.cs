namespace VMentory.Core.Secrets;

// Provider abstraction for recoverable secrets (ENG-0002). Call sites depend on this interface,
// never on the mechanism — the store can evolve (Vault, Azure KV) without touching providers.
// v1 default impl: AES-256-GCM app-native envelope encryption, DEK wrapped by a runtime-injected KEK.
public interface ISecretStore
{
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
