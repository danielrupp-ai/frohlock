using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using FrohLock.Core.Models;

namespace FrohLock.Core.Server;

/// <summary>
/// Client für die Admin-Brücke. Alle sicherheitsrelevanten Antworten (Config, Befehle)
/// sind server-signiert und werden getrennt geprüft (SignedConfigStore / RsaSignatureVerifier).
/// </summary>
public sealed class ServerClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _deviceId;
    private readonly string? _token;

    public ServerClient(HttpClient http, string baseUrl, string deviceId, string? token)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _deviceId = deviceId;
        _token = token;
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public sealed record RegisterResult(string DeviceId, string Token);

    /// <summary>Koppelt das Gerät per Kopplungscode und erhält ein Geräte-Token.</summary>
    public async Task<RegisterResult?> RegisterAsync(string pairingCode, string deviceName, CancellationToken ct = default)
    {
        var body = Json.Serialize(new { pairingCode, deviceName });
        using var resp = await _http.PostAsync($"{_baseUrl}/devices/register",
            new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Json.Deserialize<RegisterResult>(json);
    }

    /// <summary>Holt das signierte Config-Paket (oder null wenn unverändert/fehlend).</summary>
    public async Task<SignedEnvelope?> GetConfigAsync(long haveVersion, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/devices/{_deviceId}/config?have={haveVersion}", ct)
            .ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotModified) return null;
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Json.Deserialize<SignedEnvelope>(json);
    }

    /// <summary>Sendet den Heartbeat/Status.</summary>
    public async Task HeartbeatAsync(DeviceStatus status, CancellationToken ct = default)
    {
        var body = Json.Serialize(status);
        using var resp = await _http.PostAsync($"{_baseUrl}/devices/{_deviceId}/heartbeat",
            new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        _ = resp;
    }

    /// <summary>Holt ausstehende, signierte Fernbefehle.</summary>
    public async Task<List<SignedEnvelope>> GetCommandsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/devices/{_deviceId}/commands", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return new();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Json.Deserialize<List<SignedEnvelope>>(json) ?? new();
    }

    /// <summary>Bestätigt Ausführung eines Befehls (idempotentes Aufräumen serverseitig).</summary>
    public async Task AckCommandAsync(string commandId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"{_baseUrl}/devices/{_deviceId}/commands/{commandId}/ack",
            new StringContent("{}", Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        _ = resp;
    }

    public sealed record UpdateManifest(string Version, string Url, string Sha256, string SignatureBase64);

    /// <summary>Fragt das Update-Manifest ab.</summary>
    public async Task<UpdateManifest?> GetUpdateManifestAsync(string channel, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/updates/manifest?channel={channel}", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Json.Deserialize<UpdateManifest>(json);
    }
}
