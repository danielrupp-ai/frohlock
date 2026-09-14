using System.Security.Cryptography;
using System.Text;
using FrohLock.Core.Models;

namespace FrohLock.Core.Crypto;

/// <summary>
/// Prüft server-signierte Nutzlasten gegen einen fest eingebauten Public Key (PEM).
/// Verfahren: RSASSA-PSS über SHA-256. Der private Schlüssel liegt ausschließlich auf dem Server.
/// </summary>
public sealed class RsaSignatureVerifier
{
    private readonly RSA _rsa;

    public RsaSignatureVerifier(string publicKeyPem)
    {
        _rsa = RSA.Create();
        _rsa.ImportFromPem(publicKeyPem);
    }

    public bool Verify(byte[] payload, byte[] signature)
    {
        try
        {
            return _rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Prüft einen Umschlag und gibt die verifizierte Nutzlast zurück (oder null bei ungültig).</summary>
    public byte[]? Open(SignedEnvelope env)
    {
        byte[] payload, sig;
        try
        {
            payload = Convert.FromBase64String(env.PayloadBase64);
            sig = Convert.FromBase64String(env.SignatureBase64);
        }
        catch { return null; }
        return Verify(payload, sig) ? payload : null;
    }

    public T? OpenAs<T>(SignedEnvelope env)
    {
        var payload = Open(env);
        if (payload is null) return default;
        return Json.Deserialize<T>(payload);
    }
}
