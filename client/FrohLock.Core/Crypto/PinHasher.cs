using System.Security.Cryptography;

namespace FrohLock.Core.Crypto;

/// <summary>PIN wird nie im Klartext gespeichert: PBKDF2-HMAC-SHA256 mit zufälligem Salt.</summary>
public static class PinHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    public const int DefaultIterations = 210_000;

    public static (string hashB64, string saltB64) Hash(string pin, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string pin, string hashB64, string saltB64, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(hashB64) || string.IsNullOrEmpty(saltB64)) return false;
        try
        {
            var salt = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                System.Text.Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
