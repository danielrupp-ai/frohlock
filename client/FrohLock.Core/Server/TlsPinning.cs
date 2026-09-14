using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FrohLock.Core.Server;

/// <summary>
/// Erzeugt einen HttpClient, der die Serveridentität zusätzlich per SPKI-SHA256-Pin absichert.
/// Ohne passenden Pin wird die Verbindung abgelehnt (Schutz gegen MITM/kompromittierte CA).
/// </summary>
public static class TlsPinning
{
    public static HttpClient CreateClient(IReadOnlyCollection<string> spkiPinsBase64, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        // Kein Pin konfiguriert -> normale Kettenprüfung (z. B. während Erst-Setup/Dev).
        if (spkiPinsBase64.Count > 0)
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                if (cert is null) return false;
                if (errors != System.Net.Security.SslPolicyErrors.None) return false;
                var pin = SpkiSha256Base64(cert);
                return spkiPinsBase64.Contains(pin);
            };
        }

        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>SPKI-SHA256 als Base64 – identisch zu `openssl ... | openssl dgst -sha256 -binary | base64`.</summary>
    public static string SpkiSha256Base64(X509Certificate2 cert)
    {
        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        return Convert.ToBase64String(hash);
    }
}
