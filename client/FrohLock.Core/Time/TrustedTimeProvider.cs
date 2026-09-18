using System.Net.Http;
using System.Text.Json;

namespace FrohLock.Core.Time;

/// <summary>
/// Liefert eine gegen Manipulation abgesicherte, aber alltagstaugliche Zeit:
///   Grundlage ist die System-Uhr (läuft auch durch Schlaf/Standby korrekt weiter)
///   + eine NTP-Korrektur (Offset). Zusätzlich ein „Boden" (Floor): die Zeit kann
///   ohne NTP nie ZURÜCK laufen (Schutz gegen Uhr-Zurückstellen). Ein erfolgreicher
///   NTP-Sync ist maßgeblich und korrigiert Manipulation in beide Richtungen.
///
/// Warum nicht rein monoton? Ein monotoner Zähler (QPC/TickCount) zählt die Schlafzeit
/// NICHT mit → nach dem Aufwachen „hängt" die Zeit und Sperren enden zu spät. Die
/// System-Uhr springt durch den Schlaf korrekt; Manipulation fangen Floor + NTP ab.
/// </summary>
public sealed class TrustedTimeProvider : ITrustedClock
{
    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly string[] _ntpServers;
    private readonly Func<string?> _serverBaseUrlProvider;
    private readonly HttpClient _http;

    private TimeSpan _offset = TimeSpan.Zero;   // NTP-UTC - System-UTC
    private DateTime _floorUtc = DateTime.MinValue;
    private DateTime _lastSyncTrustedUtc = DateTime.MinValue;
    private DateTime _lastPersistedFloor = DateTime.MinValue;
    private bool _hasSynced;

    public TrustedTimeProvider(
        string statePath,
        Func<string?> serverBaseUrlProvider,
        HttpClient? http = null,
        string[]? ntpServers = null)
    {
        _statePath = statePath;
        _serverBaseUrlProvider = serverBaseUrlProvider;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _ntpServers = ntpServers ?? new[]
        {
            "time.cloudflare.com",
            "pool.ntp.org",
            "time.google.com",
            "ptbtime1.ptb.de"
        };
        LoadPersistedFloor();
    }

    public bool HasTime
    {
        get { lock (_gate) return _hasSynced || _floorUtc != DateTime.MinValue; }
    }

    public DateTime UtcNow
    {
        get
        {
            lock (_gate)
            {
                var raw = DateTime.UtcNow + _offset;
                var t = raw > _floorUtc ? raw : _floorUtc;  // nie unter den Boden (Anti-Rückstellen)
                if (t > _floorUtc) _floorUtc = t;
                // Boden gelegentlich persistieren (nicht bei jedem Aufruf).
                if (_floorUtc - _lastPersistedFloor > TimeSpan.FromMinutes(5))
                {
                    _lastPersistedFloor = _floorUtc;
                    PersistFloor(_floorUtc);
                }
                return t;
            }
        }
    }

    /// <summary>
    /// Nativ-Betrieb: Die System-Uhr gilt IMMER als aktuell – FrohLock funktioniert komplett
    /// OHNE Internet. Deshalb ist die „Zeit" nie „zu alt" (kein Fail-Secure wegen fehlendem Netz).
    /// Manipulation nach HINTEN fängt der Floor ab; nach VORNE korrigiert der nächste Online-Sync.
    /// </summary>
    public TimeSpan Age => TimeSpan.Zero;

    /// <summary>Wurde schon einmal eine Netz-Zeit bestätigt? (nur fürs Sync-Intervall, nicht fürs Sperren)</summary>
    public bool HasSynced { get { lock (_gate) return _hasSynced; } }

    /// <summary>Holt Netz-Zeit (NTP, dann HTTPS-Date) und macht sie maßgeblich. True bei Erfolg.</summary>
    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        var t = await QueryNtpAsync(ct).ConfigureAwait(false)
                ?? await QueryHttpsDateAsync(ct).ConfigureAwait(false);
        if (t is null) return false;
        AcceptNetworkTime(t.Value);
        return true;
    }

    /// <summary>
    /// Übernimmt eine extern ermittelte Netz-Zeit (z. B. den Date-Header eines erfolgreichen
    /// Server-Kontakts). Damit synchronisiert die Zeit auch dann, wenn NTP und der separate
    /// HTTPS-HEAD im Netz des Geräts blockiert sind – solange der Server überhaupt erreichbar ist.
    /// </summary>
    public void AcceptNetworkTime(DateTime networkUtc)
    {
        if (networkUtc == default) return;
        lock (_gate)
        {
            _offset = networkUtc - DateTime.UtcNow;   // System-Uhr-Korrektur
            _floorUtc = networkUtc;                   // Netz-Zeit ist maßgeblich
            _lastSyncTrustedUtc = networkUtc;
            _hasSynced = true;
            _lastPersistedFloor = networkUtc;
            PersistFloor(networkUtc);
        }
    }

    private async Task<DateTime?> QueryNtpAsync(CancellationToken ct)
    {
        foreach (var server in _ntpServers)
        {
            var r = await NtpClient.QueryAsync(server, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            if (r is not null) return r;
        }
        return null;
    }

    private async Task<DateTime?> QueryHttpsDateAsync(CancellationToken ct)
    {
        var baseUrl = _serverBaseUrlProvider();
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, baseUrl);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.Headers.Date is { } d) return d.UtcDateTime;
        }
        catch { }
        return null;
    }

    private void LoadPersistedFloor()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
            if (doc.RootElement.TryGetProperty("lastGoodUnix", out var el))
            {
                _floorUtc = DateTimeOffset.FromUnixTimeSeconds(el.GetInt64()).UtcDateTime;
                _lastPersistedFloor = _floorUtc;
                // Noch kein frischer NTP-Sync -> Age = MaxValue erzwingt frühen Sync.
                _hasSynced = false;
            }
        }
        catch { }
    }

    private void PersistFloor(DateTime utc)
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new { lastGoodUnix = new DateTimeOffset(utc).ToUnixTimeSeconds() });
            File.WriteAllText(_statePath, json);
        }
        catch { }
    }
}
