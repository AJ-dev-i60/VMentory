using System.Security.Cryptography;

namespace VMentory.Core.Auth;

// PBKDF2-SHA256, BCL-only (no NuGet). Format: iterations:base64(salt):base64(hash).
// Iterations stored in the hash so the work factor can be bumped on next login without invalidating old hashes.
public static class PasswordHasher
{
    private const int SaltSize  = 16;
    private const int HashSize  = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algo = HashAlgorithmName.SHA256;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algo, HashSize);
        return $"{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations)) return false;
        var salt     = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual   = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algo, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
