using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VMentory.Core.Persistence;

namespace VMentory.Core.Secrets;

// Scoped ISecretStore backed by SQLite + AES-256-GCM envelope encryption (ENG-0002).
// Each value gets its own random nonce; the DEK is loaded once per process via DekProvider (Singleton).
// The ciphertext column stores cipher || tag (last 16 bytes) as base64.
public sealed class AesGcmSecretStore(VMentoryDbContext db, DekProvider dekProvider) : ISecretStore
{
    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var dek   = await dekProvider.GetAsync(db, ct);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag    = new byte[16];
        using (var aes = new AesGcm(dek, tagSizeInBytes: 16))
            aes.Encrypt(nonce, plain, cipher, tag);

        var blob  = Convert.ToBase64String([.. cipher, .. tag]);
        var nonce64 = Convert.ToBase64String(nonce);
        var now   = DateTimeOffset.UtcNow;

        var existing = await db.Secrets.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is not null)
        {
            existing.Ciphertext = blob;
            existing.Nonce      = nonce64;
            existing.UpdatedAt  = now;
        }
        else
        {
            db.Secrets.Add(new SecretEntity
            {
                Key        = key,
                Ciphertext = blob,
                Nonce      = nonce64,
                CreatedAt  = now,
                UpdatedAt  = now,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var entity = await db.Secrets.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (entity is null) return null;

        var dek    = await dekProvider.GetAsync(db, ct);
        var nonce  = Convert.FromBase64String(entity.Nonce);
        var blob   = Convert.FromBase64String(entity.Ciphertext);
        var cipher = blob[..^16];
        var tag    = blob[^16..];
        var plain  = new byte[cipher.Length];
        using (var aes = new AesGcm(dek, tagSizeInBytes: 16))
            aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var entity = await db.Secrets.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (entity is null) return;
        db.Secrets.Remove(entity);
        await db.SaveChangesAsync(ct);
    }
}
