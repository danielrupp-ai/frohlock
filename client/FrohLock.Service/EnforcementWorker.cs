using System.Net.Http;
using System.Reflection;
using FrohLock.Core;
using FrohLock.Core.Config;
using FrohLock.Core.Crypto;
using FrohLock.Core.Logging;
using FrohLock.Core.Models;
using FrohLock.Core.Server;
using FrohLock.Core.Time;
using FrohLock.Service.Interop;
using FrohLock.Service.Update;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrohLock.Service;

/// <summary>
/// Herzschlag des Dienstes: hält Vertrauenszeit aktuell, pollt die Brücke (Config/Befehle),
/// meldet Status, startet den Overlay-Agent nach (Watchdog) und stößt Updates an.
/// Sparsam: kurze Ticks, echte Arbeit nur nach Intervall.
/// </summary>
public sealed class EnforcementWorker : BackgroundService
{
    private readonly EnforcementController _controller;
    private readonly SignedConfigStore _configStore;
    private readonly TrustedTimeProvider _time;
    private readonly RsaSignatureVerifier _verifier;
    private readonly AuditLog _audit;
    private readonly RuntimeState _state;
    private readonly ILogger<EnforcementWorker> _log;

    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private const int TickSeconds = 5;
    private DateTime _lastServerPollUtc = DateTime.MinValue;
    private DateTime _lastTimeSyncAttemptUtc = DateTime.MinValue;
    private DateTime _lastAgentCheckUtc = DateTime.MinValue;
    private DateTime _lastUsageSaveUtc = DateTime.MinValue;

    public EnforcementWorker(EnforcementController controller, SignedConfigStore configStore,
        TrustedTimeProvider time, RsaSignatureVerifier verifier, AuditLog audit, RuntimeState state,
        ILogger<EnforcementWorker> log)
    {
        _controller = controller;
        _configStore = configStore;
        _time = time;
        _verifier = verifier;
        _audit = audit;
        _state = state;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _state.LastBootUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _state.Save();
        _controller.SetConfig(_configStore.Load());
        _audit.Write("SERVICE", $"Start v{AppVersion}, Config v{_controller.Config?.ConfigVersion ?? 0}");

        // Sofort einmal Zeit synchronisieren (nicht blockierend fürs Enforcement).
        _ = _time.SyncAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                if ((now - _lastTimeSyncAttemptUtc) > TimeSpan.FromMinutes(TimeSyncIntervalMinutes()))
                {
                    _lastTimeSyncAttemptUtc = now;
                    _ = SafeSyncTimeAsync(ct);
                }

                var pollCtx = GetPollContext();
                if (pollCtx is not null && (now - _lastServerPollUtc) > TimeSpan.FromSeconds(pollCtx.PollSeconds))
                {
                    _lastServerPollUtc = now;
                    await PollServerAsync(pollCtx, ct).ConfigureAwait(false);
                }

                if ((now - _lastAgentCheckUtc) > TimeSpan.FromSeconds(15))
                {
                    _lastAgentCheckUtc = now;
                    WatchdogAgent();
                }

                // Tages-Nutzung zählen: nur wenn Kind angemeldet UND Gerät gerade nutzbar.
                if (SessionLauncher.HasActiveUserSession()
                    && _controller.AgentSilence < TimeSpan.FromSeconds(45)
                    && !_controller.Decide().IsLocked)
                {
                    _controller.AccrueUsage(TickSeconds);
                }
                if ((now - _lastUsageSaveUtc) > TimeSpan.FromSeconds(30))
                {
                    _lastUsageSaveUtc = now;
                    _state.Save();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Worker-Tick Fehler");
            }

            await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }
    }

    private int TimeSyncIntervalMinutes()
    {
        // Häufiger synchronisieren, solange noch keine frische Netzzeit vorliegt.
        return _time.HasTime && _time.Age < TimeSpan.FromHours(2) ? 30 : 3;
    }

    private async Task SafeSyncTimeAsync(CancellationToken ct)
    {
        try
        {
            bool ok = await _time.SyncAsync(ct).ConfigureAwait(false);
            if (!ok) _log.LogDebug("Zeit-Sync fehlgeschlagen (offline?)");
        }
        catch { }
    }

    private string? ReadToken()
    {
        try { return File.Exists(AppPaths.DeviceTokenPath) ? File.ReadAllText(AppPaths.DeviceTokenPath).Trim() : null; }
        catch { return null; }
    }

    /// <summary>Poll-Parameter aus Config (bevorzugt) oder Bootstrap (vor der ersten Config).</summary>
    private sealed record PollContext(string BaseUrl, string DeviceId, List<string> TlsPins,
        long ConfigVersion, int PollSeconds, int UnlockGrace);

    private PollContext? GetPollContext()
    {
        var cfg = _controller.Config;
        if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.ServerBaseUrl))
            return new PollContext(cfg.ServerBaseUrl, cfg.DeviceId, cfg.TlsSpkiPins,
                cfg.ConfigVersion, Math.Max(15, cfg.ServerPollSeconds), cfg.UnlockGraceMinutes);

        // Noch keine Config -> Bootstrap.
        try
        {
            if (File.Exists(AppPaths.BootstrapPath))
            {
                var b = Json.Deserialize<BootstrapInfo>(File.ReadAllText(AppPaths.BootstrapPath));
                if (b is not null && !string.IsNullOrWhiteSpace(b.ServerBaseUrl))
                    return new PollContext(b.ServerBaseUrl, b.DeviceId, b.TlsSpkiPins, 0, 30, 60);
            }
        }
        catch { }
        return null;
    }

    private async Task PollServerAsync(PollContext ctx, CancellationToken ct)
    {
        var token = ReadToken();
        if (string.IsNullOrEmpty(token)) return; // noch nicht gekoppelt

        using var http = TlsPinning.CreateClient(ctx.TlsPins, TimeSpan.FromSeconds(15));
        var client = new ServerClient(http, ctx.BaseUrl, ctx.DeviceId, token);
        long haveVersion = ctx.ConfigVersion;

        // 1) Neue Config?
        try
        {
            var env = await client.GetConfigAsync(haveVersion, ct).ConfigureAwait(false);
            if (env is not null)
            {
                var applied = _configStore.Apply(env);
                if (applied is not null)
                {
                    _controller.SetConfig(applied);
                    _audit.Write("CONFIG", $"Neue Config v{applied.ConfigVersion} übernommen");
                    haveVersion = applied.ConfigVersion;
                }
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "GetConfig Fehler"); }

        // 2) Befehle
        try
        {
            var cmds = await client.GetCommandsAsync(ct).ConfigureAwait(false);
            foreach (var env in cmds)
            {
                var cmd = _verifier.OpenAs<DeviceCommand>(env);
                if (cmd is null) { _audit.Write("CMD", "Ungültige Signatur – verworfen"); continue; }
                if (cmd.ExpiresAtUnix > 0 && cmd.ExpiresAtUnix < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                { _audit.Write("CMD", $"{cmd.Type} abgelaufen – verworfen"); continue; }

                await ExecuteCommandAsync(cmd, ctx, client, haveVersion, ct).ConfigureAwait(false);
                try { await client.AckCommandAsync(cmd.CommandId, ct).ConfigureAwait(false); } catch { }
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "GetCommands Fehler"); }

        // 3) Heartbeat
        try { await client.HeartbeatAsync(_controller.BuildStatus(AppVersion), ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "Heartbeat Fehler"); }
    }

    private async Task ExecuteCommandAsync(DeviceCommand cmd, PollContext ctx, ServerClient client,
        long haveVersion, CancellationToken ct)
    {
        _audit.Write("CMD", $"Ausführen: {cmd.Type} ({cmd.CommandId})");
        switch (cmd.Type)
        {
            case CommandType.Unlock:
                _controller.RemoteUnlock(cmd.UnlockMinutes ?? ctx.UnlockGrace, "Fern-Befehl");
                break;
            case CommandType.Lock:
                _controller.ForceLock("Fern-Befehl");
                break;
            case CommandType.UpdateConfig:
            case CommandType.ResetPin:
                // PIN-Reset/Änderung fließt als neue signierte Config -> sofort erneut abrufen.
                try
                {
                    var env = await client.GetConfigAsync(haveVersion, ct).ConfigureAwait(false);
                    if (env is not null && _configStore.Apply(env) is { } applied)
                        _controller.SetConfig(applied);
                }
                catch { }
                break;
            case CommandType.Update:
                await TryUpdateAsync(ctx, client, ct).ConfigureAwait(false);
                break;
            case CommandType.Uninstall:
                AuthorizeAndUninstall();
                break;
            case CommandType.Ping:
                break;
        }
    }

    private async Task TryUpdateAsync(PollContext ctx, ServerClient client, CancellationToken ct)
    {
        using var http = TlsPinning.CreateClient(ctx.TlsPins, TimeSpan.FromMinutes(5));
        var updater = new Updater(http, _verifier, _audit);
        var manifest = await client.GetUpdateManifestAsync("stable", ct).ConfigureAwait(false);
        if (manifest is null) return;
        if (!updater.IsNewer(AppVersion, manifest.Version)) { _audit.Write("UPDATE", $"Kein Update ({AppVersion} aktuell)"); return; }
        await updater.DownloadVerifyInstallAsync(manifest, ct).ConfigureAwait(false);
    }

    /// <summary>Admin-Wipe: autorisiert die stille Deinstallation (umgeht die PIN-Sperre nur für uns).</summary>
    private void AuthorizeAndUninstall()
    {
        try
        {
            AppPaths.EnsureDataDir();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "wipe.authorized"),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            _audit.Write("UNINSTALL", "Admin-Wipe autorisiert, starte Deinstaller");

            var uninst = Path.Combine(AppPaths.InstallDir, "unins000.exe");
            if (File.Exists(uninst))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uninst,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                    UseShellExecute = false
                });
            }
        }
        catch (Exception ex) { _audit.Write("UNINSTALL", $"Fehler: {ex.Message}"); }
    }

    /// <summary>Startet den Overlay-Agent nach, wenn ein Nutzer angemeldet ist, aber kein Lebenszeichen kommt.</summary>
    private void WatchdogAgent()
    {
        try
        {
            if (!SessionLauncher.HasActiveUserSession()) return;
            if (_controller.AgentSilence < TimeSpan.FromSeconds(45)) return;

            var agentPath = Path.Combine(AppPaths.InstallDir, "FrohLockAgent.exe");
            if (!File.Exists(agentPath)) return;
            bool ok = SessionLauncher.TryLaunchInActiveSession(agentPath);
            _audit.Write("WATCHDOG", ok ? "Agent nachgestartet" : "Agent-Nachstart fehlgeschlagen");
        }
        catch (Exception ex) { _log.LogDebug(ex, "Watchdog Fehler"); }
    }
}
