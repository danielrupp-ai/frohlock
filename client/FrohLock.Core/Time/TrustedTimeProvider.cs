using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace FrohLock.Core.Time;

/// <summary>
/// Liefert eine gegen lokale Uhr-Manipulation resistente Zeit:
///   Vertrauenszeit = letzte per Netz ermittelte Zeit + monoton gemessene Laufzeit (Stopwatch).
/// Die lokale Windows-Uhr wird für Enforcement NICHT verwendet.
/// Anti-Rollback: eine persistierte "last good"-Zeit wird nie unterschritten.
/// </summary>
public sealed class TrustedTimeProvider : ITrustedClock
{
    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly string[] _ntpServers;
    private readonly Func<string?> _serverBaseUrlProvider;
    private readonly HttpClient _http;

    private DateTime _anchorUtc;          // per Netz bestätigte Zeit
    private long _anchorTs;               // Stopwatch-Zeitstempel zum Anker
    private DateTime _lastSyncUtc;        // wann zuletzt erfolgreich synchronisiert (in Vertrauenszeit)
    private bool _hasNetworkTime;

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
        get { lock (_gate) return _hasNetworkTime || _anchorUtc != default; }
    }

    public DateTime UtcNow
    {
        get
        {
            lock (_gate)
            {
                if (_anchorUtc == default)
                    return DateTime.UtcNow; // Notnagel; Enforcement behandelt große Age als Fail-Secure
                var elapsed = Stopwatch.GetElapsedTime(_anchorTs);
                return _anchorUtc + elapsed;
            }
        }
    }

    public TimeSpan Age
    {
        get
        {
            lock (_gate)
            {
                if (!_hasNetworkTime) return TimeSpan.MaxValue;
                return UtcNow - _lastSyncUtc;
            }
        }
    }

    /// <summary>Versucht Netz-Zeit zu holen (NTP zuerst, dann HTTPS-Date). Gibt true bei Erfolg.</summary>
    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        var t = await QueryNtpAsync(ct).ConfigureAwait(false)
                ?? await QueryHttpsDateAsync(ct).ConfigureAwait(false);
        if (t is null) return false;

        lock (_gate)
        {
            var candidate = t.Value;
            // Anti-Rollback: akzeptiere keine Zeit deutlich vor unserer bisherigen Vertrauenszeit.
            var floor = _anchorUtc == default ? DateTime.MinValue : UtcNow.AddMinutes(-5);
            if (candidate < floor)
                candidate = floor;

            _anchorUtc = candidate;
            _anchorTs = Stopwatch.GetTimestamp();
            _lastSyncUtc = candidate;
            _hasNetworkTime = true;
            PersistFloor(candidate);
        }
        return true;
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
                var floor = DateTimeOffset.FromUnixTimeSeconds(el.GetInt64()).UtcDateTime;
                // Als monotoner Boden: setze Anker auf floor, aber markiere NICHT als frischen Netz-Sync.
                _anchorUtc = floor;
                _anchorTs = Stopwatch.GetTimestamp();
                _lastSyncUtc = floor;
                _hasNetworkTime = false; // erzwingt frühen Sync; Age ist bis dahin MaxValue
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
