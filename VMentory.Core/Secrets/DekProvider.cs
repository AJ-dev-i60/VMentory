using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using VMentory.Core.Persistence;

namespace VMentory.Core.Secrets;

// Singleton: holds the plaintext DEK in memory after first load. The DEK is wrapped with the
// operator-provided KEK and stored in the DB; only the KEK ever leaves this host.
// Thread-safe: a SemaphoreSlim guards the one-time initialise path.
public sealed class DekProvider(byte[] kek)
{
    private byte[]? _dek;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<byte[]> GetAsync(VMentoryDbContext db, CancellationToken ct = default)
    {
        if (_dek is not null) return _dek;
        await _gate.WaitAsync(ct);
        try
        {
            if (_dek is not null) return _dek;
            _dek = await LoadOrCreateAsync(db, ct);
            return _dek;
        }
        finally { _gate.Release(); }
    }

    private async Task<byte[]> LoadOrCreateAsync(VMentoryDbContext db, CancellationToken ct)
    {
        var entity = await db.Deks.FirstOrDefaultAsync(ct);
        if (entity is not null)
            return Unwrap(Convert.FromBase64String(entity.WrappedDek), Convert.FromBase64String(entity.DekNonce));

        var dek   = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        db.Deks.Add(new DekEntity
        {
            WrappedDek = Convert.ToBase64String(Wrap(dek, nonce)),
            DekNonce   = Convert.ToBase64String(nonce),
            CreatedAt  = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return dek;
    }

    private byte[] Wrap(byte[] dek, byte[] nonce)
    {
        var cipher = new byte[dek.Length];
        var tag    = new byte[16];
        using var aes = new AesGcm(kek, tagSizeInBytes: 16);
        aes.Encrypt(nonce, dek, cipher, tag);
        return [.. cipher, .. tag];
    }

    private byte[] Unwrap(byte[] wrapped, byte[] nonce)
    {
        var cipher = wrapped[..^16];
        var tag    = wrapped[^16..];
        var dek    = new byte[cipher.Length];
        using var aes = new AesGcm(kek, tagSizeInBytes: 16);
        aes.Decrypt(nonce, cipher, tag, dek);
        return dek;
    }
}
