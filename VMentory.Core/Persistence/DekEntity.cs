namespace VMentory.Core.Persistence;

// Stores the wrapped Data Encryption Key (DEK). Always exactly one row.
// WrappedDek = base64(AES-GCM encrypt(KEK, DEK) || 16-byte tag).
public class DekEntity
{
    public int Id { get; set; }
    public string WrappedDek { get; set; } = "";  // base64(cipher || tag)
    public string DekNonce { get; set; } = "";    // base64(12-byte nonce used to wrap DEK)
    public DateTimeOffset CreatedAt { get; set; }
}
