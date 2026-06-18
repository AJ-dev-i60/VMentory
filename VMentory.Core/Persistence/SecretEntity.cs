namespace VMentory.Core.Persistence;

// Encrypted secret blob. The plaintext value never touches this table.
// Ciphertext = base64(AES-GCM cipher || 16-byte tag), encrypted with the per-DB DEK.
public class SecretEntity
{
    public int Id { get; set; }
    public string Key { get; set; } = "";         // e.g. "global_winrm", "host_cred:{hostId}"
    public string Ciphertext { get; set; } = "";  // base64(cipher || tag)
    public string Nonce { get; set; } = "";       // base64(12-byte AES-GCM nonce)
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
