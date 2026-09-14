using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FrohLock.Core.Crypto;
using FrohLock.Core.Logging;
using FrohLock.Core.Server;

namespace FrohLock.Service.Update;

/// <summary>
/// Lädt Updates NUR, wenn Manifest-Signatur (EmbeddedKeys) und SHA-256 des Pakets stimmen.
/// Installiert still über den Inno-Setup-Installer und beendet sich; der Installer startet
/// den Dienst neu. Config/State bleiben erhalten (liegen in ProgramData).
/// </summary>
public sealed class Updater
{
    private readonly HttpClient _http;
    private readonly RsaSignatureVerifier _verifier;
    private readonly AuditLog _audit;

    public Updater(HttpClient http, RsaSignatureVerifier verifier, AuditLog audit)
    {
        _http = http;
        _verifier = verifier;
        _audit = audit;
    }

    public bool IsNewer(string current, string candidate)
    {
        return Version.TryParse(current, out var c) && Version.TryParse(candidate, out var n) && n > c;
    }

    /// <summary>Prüft Manifest-Signatur: RSA über UTF8("{version}|{sha256}").</summary>
    public bool VerifyManifest(ServerClient.UpdateManifest m)
    {
        try
        {
            var signed = Encoding.UTF8.GetBytes($"{m.Version}|{m.Sha256}");
            var sig = Convert.FromBase64String(m.SignatureBase64);
            return _verifier.Verify(signed, sig);
        }
        catch { return false; }
    }

    public async Task<bool> DownloadVerifyInstallAsync(ServerClient.UpdateManifest m, CancellationToken ct)
    {
        if (!VerifyManifest(m))
        {
            _audit.Write("UPDATE", $"Manifest-Signatur ungültig für {m.Version} – abgelehnt");
            return false;
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"FrohLock_{m.Version}.exe");
        try
        {
            using (var resp = await _http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false)))
                .ToLowerInvariant();
            if (!string.Equals(actual, m.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
            {
                _audit.Write("UPDATE", $"SHA256-Mismatch {m.Version} – abgebrochen");
                TryDelete(tmp);
                return false;
            }

            _audit.Write("UPDATE", $"Installiere {m.Version} still");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tmp,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(psi);
            // Installer stoppt/ersetzt/startet den Dienst neu.
            return true;
        }
        catch (Exception ex)
        {
            _audit.Write("UPDATE", $"Fehler: {ex.Message}");
            TryDelete(tmp);
            return false;
        }
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
