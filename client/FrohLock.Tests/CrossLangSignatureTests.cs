using FrohLock.Core;
using FrohLock.Core.Crypto;
using FrohLock.Core.Models;
using Xunit;

namespace FrohLock.Tests;

/// <summary>
/// Beweist, dass eine vom Python-Server (mit dem Dev-Privatschlüssel) signierte Nutzlast
/// vom C#-Client (EmbeddedKeys = Dev-Publickey) akzeptiert wird. Läuft nur, wenn
/// FROHLOCK_XLANG_JSON auf die vom Server erzeugte Datei zeigt – sonst übersprungen (CI ohne Python).
/// </summary>
public class CrossLangSignatureTests
{
    [Fact]
    public void Python_signed_config_and_command_verify_in_dotnet()
    {
        var path = Environment.GetEnvironmentVariable("FROHLOCK_XLANG_JSON");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return; // weicher Skip

        var doc = Json.Deserialize<System.Text.Json.JsonElement>(File.ReadAllText(path));
        var verifier = new RsaSignatureVerifier(EmbeddedKeys.SigningPublicKeyPem);

        var configEnv = Json.Deserialize<SignedEnvelope>(doc.GetProperty("config").GetRawText())!;
        var cfg = verifier.OpenAs<LockConfig>(configEnv);
        Assert.NotNull(cfg);
        Assert.Equal(7, cfg!.ConfigVersion);
        Assert.Equal("xlang-test", cfg.DeviceId);
        Assert.Single(cfg.Windows);
        Assert.Equal(1260, cfg.Windows[0].StartMinute);

        var cmdEnv = Json.Deserialize<SignedEnvelope>(doc.GetProperty("command").GetRawText())!;
        var cmd = verifier.OpenAs<DeviceCommand>(cmdEnv);
        Assert.NotNull(cmd);
        Assert.Equal(CommandType.Unlock, cmd!.Type);
        Assert.Equal(45, cmd.UnlockMinutes);
    }
}
