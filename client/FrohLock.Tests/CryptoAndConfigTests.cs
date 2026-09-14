using System.Security.Cryptography;
using FrohLock.Core;
using FrohLock.Core.Config;
using FrohLock.Core.Crypto;
using FrohLock.Core.Models;
using Xunit;

namespace FrohLock.Tests;

public class CryptoAndConfigTests
{
    [Fact]
    public void PinHash_roundtrip_and_reject_wrong()
    {
        var (hash, salt) = PinHasher.Hash("1234");
        Assert.True(PinHasher.Verify("1234", hash, salt));
        Assert.False(PinHasher.Verify("9999", hash, salt));
        Assert.DoesNotContain("1234", hash); // nie Klartext
    }

    private static (RSA rsa, string pubPem) NewKey()
    {
        var rsa = RSA.Create(2048);
        var pub = rsa.ExportSubjectPublicKeyInfoPem();
        return (rsa, pub);
    }

    private static SignedEnvelope Sign<T>(RSA rsa, T value)
    {
        var payload = Json.Canonical(value);
        var sig = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new SignedEnvelope
        {
            PayloadBase64 = Convert.ToBase64String(payload),
            SignatureBase64 = Convert.ToBase64String(sig)
        };
    }

    [Fact]
    public void Rsa_sign_verify_roundtrip()
    {
        var (rsa, pub) = NewKey();
        var verifier = new RsaSignatureVerifier(pub);
        var cmd = new DeviceCommand { Type = CommandType.Unlock, DeviceId = "d1", UnlockMinutes = 30 };
        var env = Sign(rsa, cmd);
        var opened = verifier.OpenAs<DeviceCommand>(env);
        Assert.NotNull(opened);
        Assert.Equal(CommandType.Unlock, opened!.Type);
    }

    [Fact]
    public void Rsa_rejects_tampered_payload()
    {
        var (rsa, pub) = NewKey();
        var verifier = new RsaSignatureVerifier(pub);
        var env = Sign(rsa, new DeviceCommand { Type = CommandType.Unlock });
        // Nutzlast manipulieren.
        var bytes = Convert.FromBase64String(env.PayloadBase64);
        bytes[0] ^= 0xFF;
        env.PayloadBase64 = Convert.ToBase64String(bytes);
        Assert.Null(verifier.OpenAs<DeviceCommand>(env));
    }

    [Fact]
    public void Rsa_rejects_foreign_key()
    {
        var (rsa1, _) = NewKey();
        var (_, pub2) = NewKey();
        var verifier = new RsaSignatureVerifier(pub2); // anderer Public Key
        var env = Sign(rsa1, new DeviceCommand { Type = CommandType.Uninstall });
        Assert.Null(verifier.OpenAs<DeviceCommand>(env));
    }

    [Fact]
    public void ConfigStore_applies_and_blocks_rollback()
    {
        var (rsa, pub) = NewKey();
        var verifier = new RsaSignatureVerifier(pub);
        var path = Path.Combine(Path.GetTempPath(), "frohlock_test_" + Guid.NewGuid().ToString("N"), "config.json");
        var store = new SignedConfigStore(path, verifier);

        var v2 = Sign(rsa, new LockConfig { ConfigVersion = 2, DeviceId = "d1" });
        Assert.NotNull(store.Apply(v2));
        Assert.Equal(2, store.Load()!.ConfigVersion);

        // Ältere Version wird abgelehnt (Anti-Rollback).
        var v1 = Sign(rsa, new LockConfig { ConfigVersion = 1, DeviceId = "d1" });
        Assert.Null(store.Apply(v1));
        Assert.Equal(2, store.Load()!.ConfigVersion);

        // Neuere Version wird übernommen.
        var v3 = Sign(rsa, new LockConfig { ConfigVersion = 3, DeviceId = "d1" });
        Assert.NotNull(store.Apply(v3));
        Assert.Equal(3, store.Load()!.ConfigVersion);

        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
    }

    [Fact]
    public void ConfigStore_rejects_unsigned_garbage()
    {
        var (_, pub) = NewKey();
        var verifier = new RsaSignatureVerifier(pub);
        var path = Path.Combine(Path.GetTempPath(), "frohlock_test_" + Guid.NewGuid().ToString("N"), "config.json");
        var store = new SignedConfigStore(path, verifier);
        var bad = new SignedEnvelope { PayloadBase64 = Convert.ToBase64String(Json.Canonical(new LockConfig { ConfigVersion = 5 })), SignatureBase64 = "AAAA" };
        Assert.Null(store.Apply(bad));
        Assert.Null(store.Load());
    }
}
