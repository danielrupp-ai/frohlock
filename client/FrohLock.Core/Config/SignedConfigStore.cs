using FrohLock.Core.Crypto;
using FrohLock.Core.Models;

namespace FrohLock.Core.Config;

/// <summary>
/// Lädt/speichert die Gerätekonfiguration ausschließlich als server-signiertes Paket.
/// Ungültige Signatur oder niedrigere ConfigVersion (Anti-Rollback) -&gt; abgelehnt.
/// </summary>
public sealed class SignedConfigStore
{
    private readonly string _path;
    private readonly RsaSignatureVerifier _verifier;

    public SignedConfigStore(string path, RsaSignatureVerifier verifier)
    {
        _path = path;
        _verifier = verifier;
    }

    /// <summary>Liest die aktuell gespeicherte, gültige Config. Null wenn keine/ungültig.</summary>
    public LockConfig? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var env = Json.Deserialize<SignedEnvelope>(File.ReadAllText(_path));
            if (env is null) return null;
            return _verifier.OpenAs<LockConfig>(env);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Übernimmt ein neues signiertes Config-Paket, wenn Signatur gültig und Version höher ist.
    /// Gibt die übernommene Config zurück oder null bei Ablehnung.
    /// </summary>
    public LockConfig? Apply(SignedEnvelope env)
    {
        var incoming = _verifier.OpenAs<LockConfig>(env);
        if (incoming is null) return null; // ungültige Signatur

        var current = Load();
        if (current is not null && incoming.ConfigVersion < current.ConfigVersion)
            return null; // Anti-Rollback

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, Json.Serialize(env));
        return incoming;
    }
}
