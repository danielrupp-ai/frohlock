using System.Net.Http;
using System.Reflection;
using FrohLock.Core;
using FrohLock.Core.Config;
using FrohLock.Core.Crypto;
using FrohLock.Core.Logging;
using FrohLock.Core.Models;
using FrohLock.Core.Schedule;
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
    private DateTime _lastCmdPollUtc = DateTime.MinValue;
    private DateTime _lastTimeSyncAttemptUtc = DateTime.MinValue;
    private DateTime _lastAgentCheckUtc = DateTime.MinValue;
    private DateTime _lastUsageSaveUtc = DateTime.MinValue;
    private DateTime _lastLoopSystemUtc = DateTime.MinValue;
    private DateTime _lastPenaltyTickUtc = DateTime.MinValue;
    private long _lastLoopAwakeTick100ns;
    private long _lastLoopAwake100ns;            // für den Wächter-Thread (Volatile)
    private volatile bool _forceTimeSync;

    /// <summary>So lange darf die Hauptschleife bei wachem Laptop stehen, dann Neustart.</summary>
    private const int LoopStallLimitSeconds = 120;
    /// <summary>Hartes Zeitlimit je Netz-Runde (Updates laden länger, laufen aber in eigener Runde weiter).</summary>
    private const int NetworkRoundLimitSeconds = 120;

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
        _controller.SetConfig(_configStore.Load());
        _audit.Write("SERVICE", $"Start v{AppVersion}, Config v{_controller.Config?.ConfigVersion ?? 0}");
        CheckGapSinceLastRun();
        _state.Save();

        // Sofort einmal Zeit synchronisieren (nicht blockierend fürs Enforcement).
        _ = _time.SyncAsync(ct);

        // Netz (Server, Befehle, Zeit-Sync, Updates) läuft GETRENNT: eine hängende Verbindung darf
        // Zählen und Sperren nie anhalten.
        _ = Task.Run(() => NetworkLoopAsync(ct), ct);
        StartLoopWatchdog(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                long awake = AwakeClock.Now100ns;
                Volatile.Write(ref _lastLoopAwake100ns, awake);

                // Lücke erkennen: Schleife war viel länger als ein Tick weg. Die Windows-Uhr ohne
                // Schlafzeit sagt, ob der Laptop geschlafen hat oder wach war, während der Dienst stand.
                if (_lastLoopSystemUtc != DateTime.MinValue)
                {
                    double wallGap = (now - _lastLoopSystemUtc).TotalSeconds;
                    double awakeGap = (awake - _lastLoopAwakeTick100ns) / 10_000_000.0;
                    switch (GapAnalysis.Classify(wallGap, awakeGap))
                    {
                        case GapKind.Slept:
                            _audit.Write("TIME", $"Aufwachen erkannt (Laptop schlief {GapAnalysis.Duration((long)(wallGap - awakeGap))}) – erzwinge Zeit-Sync");
                            _forceTimeSync = true;
                            break;
                        case GapKind.StalledAwake:
                            _controller.RegisterStalledGap(
                                new DateTimeOffset(now).ToUnixTimeSeconds() - (long)Math.Min(wallGap, awakeGap),
                                new DateTimeOffset(now).ToUnixTimeSeconds(), (long)awakeGap, "hing");
                            _forceTimeSync = true;
                            break;
                    }
                }
                _lastLoopSystemUtc = now;
                _lastLoopAwakeTick100ns = awake;
                _controller.RecordTick(now, awake);

                if ((now - _lastAgentCheckUtc) > TimeSpan.FromSeconds(15))
                {
                    _lastAgentCheckUtc = now;
                    WatchdogAgent();
                    _controller.ResolvePendingGaps();
                }

                // Auszeit-Countdown: läuft nur, solange das Kind sie wirklich sieht (Bildschirm an, Overlay da).
                int elapsed = _lastPenaltyTickUtc == DateTime.MinValue
                    ? TickSeconds
                    : (int)Math.Clamp((now - _lastPenaltyTickUtc).TotalSeconds, 0, TickSeconds * 2);
                _lastPenaltyTickUtc = now;
                _controller.TickPenalty(elapsed,
                    SessionLauncher.HasActiveUserSession(),
                    _controller.AgentSilence < TimeSpan.FromSeconds(45),
                    _controller.ScreenActive);

                // Tages-Nutzung zählen: nur wenn Kind angemeldet, Gerät nutzbar UND der Bildschirm
                // wirklich an ist (zugeklappt / Bildschirm aus / Windows gesperrt zählt NICHT).
                // Den Bildschirmzustand meldet der Agent; schweigt er (Standby), zählt ohnehin nichts.
                if (UsageGate.ShouldCount(
                        SessionLauncher.HasActiveUserSession(),
                        _controller.AgentSilence < TimeSpan.FromSeconds(45),
                        _controller.Decide().IsLocked,
                        _controller.ScreenActive))
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

    /// <summary>
    /// Nach einem (Neu-)Start: lief der Dienst eine Weile gar nicht, obwohl der Laptop wach war
    /// (beendet, abgestürzt, angehalten)? Gleiche Windows-Sitzung erkennt man daran, dass die Uhr
    /// ohne Schlafzeit seit dem letzten Takt weitergelaufen ist.
    /// </summary>
    private void CheckGapSinceLastRun()
    {
        try
        {
            var (lastWall, lastAwake) = _controller.LastTick;
            if (lastWall <= 0 || lastAwake <= 0) return;
            long nowWall = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double awakeGap = AwakeClock.SecondsSince(lastAwake);
            double wallGap = nowWall - lastWall;
            // Neustart des PCs: Uhr ohne Schlafzeit beginnt bei 0 -> kleiner als zuletzt bzw. größer als die echte Lücke.
            bool sameWindowsRun = awakeGap > 0 && awakeGap <= wallGap + 60;
            if (!sameWindowsRun) return;
            if (GapAnalysis.Classify(wallGap, awakeGap) == GapKind.StalledAwake)
                _controller.RegisterStalledGap(nowWall - (long)Math.Min(wallGap, awakeGap), nowWall, (long)awakeGap, "aus");
        }
        catch (Exception ex) { _log.LogDebug(ex, "Lücken-Prüfung beim Start fehlgeschlagen"); }
    }

    /// <summary>
    /// Wächter auf eigenem Thread: steht die Hauptschleife bei WACHEM Laptop länger als 2 Minuten,
    /// beendet sich der Dienst hart – Windows startet ihn nach 5 s neu (Wiederherstellungs-Aktion).
    /// Schlafen zählt nicht mit (Uhr ohne Schlafzeit), ein Standby löst also nichts aus.
    /// </summary>
    private void StartLoopWatchdog(CancellationToken ct)
    {
        var t = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));
                long last = Volatile.Read(ref _lastLoopAwake100ns);
                if (last == 0 || ct.IsCancellationRequested) continue;
                double stalled = AwakeClock.SecondsSince(last);
                if (stalled > LoopStallLimitSeconds)
                {
                    try { _audit.Write("WATCHDOG", $"Hauptschleife stand {(int)stalled} s bei wachem Laptop – Dienst startet neu"); } catch { }
                    try { _state.Save(); } catch { }
                    Environment.FailFast("FrohLock: Hauptschleife hängt");
                }
            }
        }) { IsBackground = true, Name = "FrohLock-Schleifenwaechter" };
        t.Start();
    }

    /// <summary>Server-Kontakt, Befehle und Zeit-Sync – mit hartem Zeitlimit je Runde.</summary>
    private async Task NetworkLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await NetworkRoundAsync(ct).WaitAsync(TimeSpan.FromSeconds(NetworkRoundLimitSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (TimeoutException) { _log.LogWarning("Netz-Runde dauerte zu lange – wird übersprungen"); }
            catch (Exception ex) { _log.LogDebug(ex, "Netz-Runde Fehler"); }

            await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }
    }

    private async Task NetworkRoundAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_forceTimeSync || (now - _lastTimeSyncAttemptUtc) > TimeSpan.FromMinutes(TimeSyncIntervalMinutes()))
        {
            _forceTimeSync = false;
            _lastTimeSyncAttemptUtc = now;
            _ = SafeSyncTimeAsync(ct);
        }

        var pollCtx = GetPollContext();
        if (pollCtx is not null && (now - _lastServerPollUtc) > TimeSpan.FromSeconds(pollCtx.PollSeconds))
        {
            _lastServerPollUtc = now;
            await PollServerAsync(pollCtx, ct).ConfigureAwait(false);
        }
        // Schnelle Befehls-Abfrage: Fern-Sperren/-Entsperren wirkt in ~5 s statt erst beim langsamen Poll.
        else if (pollCtx is not null && (now - _lastCmdPollUtc) > TimeSpan.FromSeconds(5))
        {
            _lastCmdPollUtc = now;
            await PollCommandsFastAsync(pollCtx, ct).ConfigureAwait(false);
        }
    }

    private int TimeSyncIntervalMinutes()
    {
        // Häufiger synchronisieren, solange noch nie eine Netzzeit bestätigt wurde
        // (korrigiert eine evtl. verstellte Uhr, sobald Internet da ist). Danach seltener.
        return _time.HasSynced ? 30 : 3;
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

        // Zeit aus dem erfolgreichen Server-Kontakt übernehmen (robust, wenn NTP blockiert ist).
        if (client.LastServerDateUtc is { } serverDate)
            _time.AcceptNetworkTime(serverDate.UtcDateTime);

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

    /// <summary>Nur Befehle holen/ausführen (schnell, alle ~5 s). Updates bleiben dem langsamen Poll überlassen.</summary>
    private async Task PollCommandsFastAsync(PollContext ctx, CancellationToken ct)
    {
        var token = ReadToken();
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            using var http = TlsPinning.CreateClient(ctx.TlsPins, TimeSpan.FromSeconds(10));
            var client = new ServerClient(http, ctx.BaseUrl, ctx.DeviceId, token);
            var cmds = await client.GetCommandsAsync(ct).ConfigureAwait(false);
            foreach (var env in cmds)
            {
                var cmd = _verifier.OpenAs<DeviceCommand>(env);
                if (cmd is null) continue;
                if (cmd.Type == CommandType.Update) continue; // Updates macht der langsame Poll
                if (cmd.ExpiresAtUnix > 0 && cmd.ExpiresAtUnix < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) continue;
                await ExecuteCommandAsync(cmd, ctx, client, ctx.ConfigVersion, ct).ConfigureAwait(false);
                try { await client.AckCommandAsync(cmd.CommandId, ct).ConfigureAwait(false); } catch { }
            }
            if (client.LastServerDateUtc is { } d) _time.AcceptNetworkTime(d.UtcDateTime);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Schnelle Befehls-Abfrage Fehler"); }
    }

    private async Task ExecuteCommandAsync(DeviceCommand cmd, PollContext ctx, ServerClient client,
        long haveVersion, CancellationToken ct)
    {
        // Doppelt zugestellte Befehle (z. B. Ack verloren) nur einmal ausführen – sonst addiert sich eine Auszeit doppelt.
        if (cmd.Type is CommandType.Timeout or CommandType.ClearTimeout
            && !_controller.MarkCommandExecuted(cmd.CommandId))
        {
            _audit.Write("CMD", $"{cmd.Type} ({cmd.CommandId}) schon ausgeführt – übersprungen");
            return;
        }
        _audit.Write("CMD", $"Ausführen: {cmd.Type} ({cmd.CommandId})");
        switch (cmd.Type)
        {
            case CommandType.Timeout:
                _controller.AddPenalty(cmd.UnlockMinutes ?? 15, "gestört ohne Notfall");
                break;
            case CommandType.ClearTimeout:
                _controller.ClearPenalty("Eltern");
                break;
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

    private CancellationTokenSource? _updateCts;
    private DateTime _updateStartedUtc = DateTime.MinValue;
    private DateTime _lastUpdateBusyAuditUtc = DateTime.MinValue;
    private static readonly TimeSpan UpdateDeadline = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Update höchstens einmal gleichzeitig – aber mit eigener Frist. Vorher hielt ein Download, der beim
    /// Zuklappen/Schlafen hängen blieb, die Sperre für immer (die Netz-Runde gab nur auf, brach ihn aber
    /// nicht ab): Danyil 24.10. – 2 Stunden „Ausführen: Update" alle 30 s, nie ein Download.
    /// </summary>
    private async Task TryUpdateAsync(PollContext ctx, ServerClient client, CancellationToken ct)
    {
        CancellationTokenSource cts;
        lock (_audit)
        {
            if (_updateCts is not null)
            {
                var age = DateTime.UtcNow - _updateStartedUtc;
                if (age < UpdateDeadline + TimeSpan.FromMinutes(1))
                {
                    if ((DateTime.UtcNow - _lastUpdateBusyAuditUtc) > TimeSpan.FromMinutes(5))
                    {
                        _lastUpdateBusyAuditUtc = DateTime.UtcNow;
                        _audit.Write("UPDATE", $"Download läuft noch (seit {(int)age.TotalMinutes} Min) – kein zweiter Start");
                    }
                    return;
                }
                _audit.Write("UPDATE", $"⚠ Hängenden Download nach {(int)age.TotalMinutes} Min abgebrochen – neuer Versuch");
                try { _updateCts.Cancel(); } catch { }
            }
            cts = _updateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(UpdateDeadline);
            _updateStartedUtc = DateTime.UtcNow;
        }
        try { await TryUpdateCoreAsync(ctx, client, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { _audit.Write("UPDATE", "Fehler: Zeitlimit beim Herunterladen – nächster Versuch beim nächsten Abruf"); }
        catch (Exception ex) { _audit.Write("UPDATE", $"Fehler: {ex.Message}"); }
        finally
        {
            lock (_audit) { if (ReferenceEquals(_updateCts, cts)) _updateCts = null; }
            cts.Dispose();
        }
    }

    private async Task TryUpdateCoreAsync(PollContext ctx, ServerClient client, CancellationToken ct)
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

    private DateTime _lastAgentLaunchUtc = DateTime.MinValue;
    private readonly Queue<DateTime> _agentLaunches = new();
    private DateTime _lastOverlayAlertUtc = DateTime.MinValue;
    private DateTime _lastDisconnectUtc = DateTime.MinValue;
    private int _launchesSinceAlive;
    private long _sessionSinceAwake100ns;

    /// <summary>
    /// Hält den Sperrbildschirm (Overlay-Agent) am Leben:
    /// schweigt er, wird ein hängender Rest beendet und neu gestartet; startet er dauerhaft nicht
    /// (abgestürzt oder ständig beendet), gibt es eine Warnung für die Eltern – und ist der Laptop
    /// gerade gesperrt, wird die Windows-Sitzung getrennt (Anmeldebildschirm), damit die Sperre greift.
    /// </summary>
    private void WatchdogAgent()
    {
        try
        {
            if (!SessionLauncher.HasActiveUserSession()) { _sessionSinceAwake100ns = 0; return; }
            if (_sessionSinceAwake100ns == 0) _sessionSinceAwake100ns = AwakeClock.Now100ns;
            var silence = _controller.AgentSilence;
            if (silence < TimeSpan.FromSeconds(45)) { _launchesSinceAlive = 0; return; }
            var now = DateTime.UtcNow;

            // Sicherheitsnetz: gesperrt, aber der Sperrbildschirm kommt trotz Nachstarts nicht ->
            // Sitzung trennen. Erst NACH mindestens zwei vergeblichen Starts (vorher wurde hier getrennt,
            // bevor überhaupt ein Nachstart versucht war). Nach dem Anmelden 2 Min Schonfrist.
            bool sessionSettled = AwakeClock.SecondsSince(_sessionSinceAwake100ns) > 120;
            if (silence > TimeSpan.FromSeconds(90) && sessionSettled && _launchesSinceAlive >= 2
                && _controller.Decide().IsLocked && (now - _lastDisconnectUtc) > TimeSpan.FromSeconds(60))
            {
                _lastDisconnectUtc = now;
                _launchesSinceAlive = 0;
                bool ok = SessionLauncher.DisconnectActiveSession();
                _audit.Write("OVERLAY", ok
                    ? "⚠ Gesperrt, aber Sperrbildschirm fehlte – Windows-Sitzung getrennt (Anmeldebildschirm)"
                    : "⚠ Gesperrt, aber Sperrbildschirm fehlte – Trennen der Sitzung fehlgeschlagen");
                return;
            }

            // Frisch gestarteten Agent erst hochfahren lassen.
            if ((now - _lastAgentLaunchUtc) < TimeSpan.FromSeconds(30)) return;

            var agentPath = Path.Combine(AppPaths.InstallDir, "FrohLockAgent.exe");
            if (!File.Exists(agentPath)) return;

            // Hängender Agent hält die Ein-Instanz-Sperre -> jeder Neustart beendet sich sofort wieder.
            int killed = KillAgentsInActiveSession();
            if (killed > 0) _audit.Write("WATCHDOG", $"Sperrbildschirm reagierte nicht – {killed} Prozess(e) beendet");

            bool launched = SessionLauncher.TryLaunchInActiveSession(agentPath);
            _lastAgentLaunchUtc = now;
            _launchesSinceAlive++;
            _audit.Write("WATCHDOG", launched ? "Agent nachgestartet" : "Agent-Nachstart fehlgeschlagen");

            _agentLaunches.Enqueue(now);
            while (_agentLaunches.Count > 0 && now - _agentLaunches.Peek() > TimeSpan.FromMinutes(10)) _agentLaunches.Dequeue();
            if (_agentLaunches.Count >= 5 && (now - _lastOverlayAlertUtc) > TimeSpan.FromMinutes(30))
            {
                _lastOverlayAlertUtc = now;
                _audit.Write("OVERLAY", $"⚠ Sperrbildschirm lief nicht ({_agentLaunches.Count} Neustarts in 10 Min) – " +
                    "abgestürzt oder wird beendet; Bildschirmzeit wird trotzdem gezählt");
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Watchdog Fehler"); }
    }

    private static int KillAgentsInActiveSession()
    {
        int killed = 0;
        uint session = SessionLauncher.ActiveConsoleSessionId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("FrohLockAgent"))
        {
            try
            {
                if (p.SessionId != session) continue;
                p.Kill();
                p.WaitForExit(3000);
                killed++;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return killed;
    }
}
