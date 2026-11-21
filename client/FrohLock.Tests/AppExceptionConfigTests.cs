using System.Security.Cryptography;
using System.Text;
using FrohLock.Core;
using FrohLock.Core.Config;
using FrohLock.Core.Crypto;
using FrohLock.Core.Models;
using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

/// <summary>
/// Konfig-Feld „appExceptions“ (Windows 0.31.0): genau so, wie der Python-Server es baut
/// (json.dumps mit separators=(",", ":"), ensure_ascii=False), signiert und über den echten Config-Store geladen.
/// </summary>
public class AppExceptionConfigTests
{
    private const string EvoSha = "d34d36f3be1a6c49c56525ae86469b92e4f417ddf0b43cf00dd80c385c4b0562";

    // Wörtlich wie configbuilder.build_full_config für ein Windows-Gerät (Feldreihenfolge egal, signiert werden die Bytes).
    private const string PythonPayload =
        "{\"configVersion\":12,\"deviceId\":\"laptop-renat\",\"windows\":[{\"id\":\"w1\",\"label\":\"Nacht\",\"days\":[0,1,2,3,4,5,6]," +
        "\"startMinute\":1260,\"endMinute\":420,\"enabled\":true}],\"dailyBudgetMinutes\":120,\"dailyBudgetByWeekday\":[]," +
        "\"unlockGraceMinutes\":60,\"pinHash\":\"aA==\",\"pinSalt\":\"aA==\",\"pinIterations\":210000," +
        "\"serverBaseUrl\":\"https://frohlock.example\",\"tlsSpkiPins\":[],\"serverPollSeconds\":30," +
        "\"maxTrustedTimeStalenessMinutes\":4320,\"failSecureLocked\":true," +
        "\"alwaysAllowedApps\":[\"org.telegram.messenger\"],\"uncountedApps\":[]," +
        "\"appExceptions\":[{\"name\":\"Evoluzzer\",\"exe\":\"Evoluzzer.exe\",\"sha256\":[\"" + EvoSha + "\"],\"mode\":\"beides\"}," +
        "{\"name\":\"Spiel ö\",\"exe\":\"Anderes.exe\",\"sha256\":[],\"mode\":\"offen\"}]," +
        "\"futureField\":{\"x\":1}}";

    private static SignedEnvelope SignRaw(RSA rsa, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return new SignedEnvelope
        {
            PayloadBase64 = Convert.ToBase64String(bytes),
            SignatureBase64 = Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        };
    }

    [Fact]
    public void Python_style_config_with_app_exceptions_verifies_and_parses()
    {
        using var rsa = RSA.Create(2048);
        var verifier = new RsaSignatureVerifier(rsa.ExportSubjectPublicKeyInfoPem());
        var path = Path.Combine(Path.GetTempPath(), "frohlock_test_" + Guid.NewGuid().ToString("N"), "config.json");
        var store = new SignedConfigStore(path, verifier);
        try
        {
            var applied = store.Apply(SignRaw(rsa, PythonPayload));
            Assert.NotNull(applied);
            var cfg = store.Load()!;
            Assert.Equal(12, cfg.ConfigVersion);
            Assert.Equal(2, cfg.AppExceptions!.Count);
            var evo = cfg.AppExceptions[0];
            Assert.Equal("Evoluzzer", evo.Name);
            Assert.Equal("Evoluzzer.exe", evo.Exe);
            Assert.Equal(EvoSha, Assert.Single(evo.Sha256!));
            Assert.Equal("beides", evo.Mode);
            Assert.Equal("Spiel ö", cfg.AppExceptions[1].Name);
            // Nur der wirksame Eintrag zählt („offen“ wirkt nicht).
            Assert.Equal("Evoluzzer.exe", Assert.Single(AppExceptionPolicy.Effective(cfg)).Exe);

            // Manipulation der Nutzlast (z. B. Art auf „beides“ für ein anderes Programm) -> Signatur ungültig.
            var tampered = SignRaw(rsa, PythonPayload);
            tampered.PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(PythonPayload.Replace("\"offen\"", "\"beides\"")));
            Assert.Null(store.Apply(tampered));
            Assert.Single(AppExceptionPolicy.Effective(store.Load()));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public void Old_config_without_field_and_odd_values_never_break_loading()
    {
        // Alte Server-Config ohne das Feld.
        var old = Json.Deserialize<LockConfig>("{\"configVersion\":3,\"pinHash\":\"aA==\"}")!;
        Assert.Empty(AppExceptionPolicy.Effective(old));
        // null-Werte und unbekannte Art -> lädt, wirkt aber nicht.
        var odd = Json.Deserialize<LockConfig>(
            "{\"configVersion\":4,\"appExceptions\":[{\"name\":null,\"exe\":\"Evoluzzer.exe\",\"sha256\":null,\"mode\":\"neu-in-v2\"}]}")!;
        Assert.Single(odd.AppExceptions!);
        Assert.Empty(AppExceptionPolicy.Effective(odd));
        var nullList = Json.Deserialize<LockConfig>("{\"configVersion\":5,\"appExceptions\":null}")!;
        Assert.Empty(AppExceptionPolicy.Effective(nullList));
    }

    [Fact]
    public void Csharp_roundtrip_keeps_fields_camel_case()
    {
        var cfg = new LockConfig
        {
            ConfigVersion = 1,
            AppExceptions = new() { new AppException { Name = "Evoluzzer", Exe = "Evoluzzer.exe", Sha256 = new() { EvoSha }, Mode = "zeit" } }
        };
        var json = Json.Serialize(cfg);
        Assert.Contains("\"appExceptions\":[{\"name\":\"Evoluzzer\",\"exe\":\"Evoluzzer.exe\",\"sha256\":[\"" + EvoSha + "\"],\"mode\":\"zeit\"}]", json);
        var back = Json.Deserialize<LockConfig>(json)!;
        Assert.Equal("zeit", back.AppExceptions![0].Mode);
    }
}
