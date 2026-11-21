using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FrohLock.Core;
using FrohLock.Core.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrohLock.Service.Ipc;

/// <summary>
/// Named-Pipe-Server (robustes Mehr-Instanzen-Muster). Der Overlay-Agent fragt hier Status
/// ab und reicht PIN-Versuche ein. PIN-Prüfung passiert im Dienst (LocalSystem).
/// Es steht IMMER eine Pipe-Instanz zum Verbinden bereit (kein „Drain"-Loch mehr).
/// </summary>
public sealed class IpcServer : BackgroundService
{
    private const int MaxServerInstances = 16; // genug für Poll + Alive + PIN gleichzeitig

    private readonly EnforcementController _controller;
    private readonly ILogger<IpcServer> _log;
    private readonly Action _onAgentAlive;

    public IpcServer(EnforcementController controller, ILogger<IpcServer> log, Action onAgentAlive)
    {
        _controller = controller;
        _log = log;
        _onAgentAlive = onAgentAlive;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipe();
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                // Diese Verbindung eigenständig bedienen; sofort weiter die nächste Instanz öffnen.
                _ = HandleAndDisposeAsync(server, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "IPC-Accept Fehler");
                server?.Dispose();
                try { await Task.Delay(500, stoppingToken).ConfigureAwait(false); } catch { }
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var pipeSecurity = new PipeSecurity();
        // Breite Verbindungsrechte (Inhalt ist nicht sensibel; PIN wird serverseitig geprüft,
        // dem Agent wird inhaltlich nicht vertraut). Schließt ACL-/Integritäts-Probleme aus.
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            AppPaths.PipeName, PipeDirection.InOut, MaxServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity);
    }

    private async Task HandleAndDisposeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is not null)
            {
                var msg = Json.Deserialize<IpcMessage>(line) ?? new IpcMessage();
                // Wer ist am anderen Ende? Nur der echte Sperrbildschirm (Program Files) darf den Vordergrund
                // melden, der das Zählen beeinflusst (Ausnahme-Programm).
                int clientPid = msg.ReportsForeground ? Interop.ProcessInfo.PipeClientPid(server.SafePipeHandle) : -1;
                var reply = Handle(msg, clientPid);
                await writer.WriteLineAsync(Json.Serialize(reply).AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                try { server.WaitForPipeDrain(); } catch { } // sicherstellen, dass der Agent die Antwort liest
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPC-Client Fehler");
        }
        finally
        {
            try { server.Dispose(); } catch { }
        }
    }

    private IpcMessage Handle(IpcMessage msg, int clientPid)
    {
        switch (msg.Kind)
        {
            case IpcKind.GetStatus:
            {
                // Ausnahme-Programm: zuerst (prüft Vordergrund + Installation), danach den Sperrzustand.
                bool trusted = clientPid > 0 && _controller.IsGenuineAgent(clientPid);
                var ex = _controller.GetExceptionView(msg.ForegroundPid, trusted, msg.LocalAppData);
                var d = _controller.Decide();
                var baseUrl = _controller.Config?.ServerBaseUrl ?? "";
                return new IpcMessage
                {
                    ExceptionAllowed = ex.Allowed && d.IsLocked,
                    ExceptionName = ex.Name,
                    ExceptionInstalled = ex.Installed,
                    ExceptionForegroundPid = ex.ForegroundPid,
                    ExceptionUncounted = ex.Uncounted,
                    Kind = IpcKind.StatusReply,
                    Locked = d.IsLocked,
                    Reason = d.Reason,
                    MinutesUntilLock = d.IsLocked ? -1 : _controller.MinutesUntilLock(),
                    ForgotUrl = string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl.TrimEnd('/') + "/forgot",
                    ScheduleSummary = _controller.TodayScheduleSummary(),
                    UsageMinutes = _controller.TodayUsageMinutes(),
                    BudgetMinutes = _controller.TodayBudgetMinutes(),
                    Configured = _controller.IsConfigured,
                    UsageSegments = _controller.TodayUsageSegments(),
                    PenaltySeconds = _controller.PenaltySecondsRemaining,
                    PenaltyCount = _controller.PenaltyCount,
                    PenaltyTotalMinutes = _controller.PenaltyTotalMinutes
                };
            }
            case IpcKind.SubmitPin:
            {
                var (ok, remaining) = _controller.SubmitPin(msg.Pin ?? "");
                return new IpcMessage { Kind = IpcKind.PinResult, Success = ok, PinFailuresRemaining = remaining };
            }
            case IpcKind.AgentAlive:
                _controller.SetScreenState(msg.DisplayOn, msg.LidOpen, msg.ExternalDisplay, msg.SessionLocked,
                    msg.ScreenOnMinutes);
                _controller.MarkAgentAlive(msg.SessionId, msg.LockVisible, msg.ForegroundOurs);
                _controller.ResolvePendingGaps();
                return new IpcMessage { Kind = IpcKind.StatusReply, Locked = _controller.Decide().IsLocked };
            case IpcKind.AgentCrashReport:
                _controller.ReportAgentCrash(msg.Reason);
                return new IpcMessage { Kind = IpcKind.StatusReply, Locked = _controller.Decide().IsLocked };
            case IpcKind.BypassReport:
                _controller.ReportCoverageProblem(msg.Reason, msg.Alert, msg.Escalate);
                return new IpcMessage { Kind = IpcKind.StatusReply, Locked = _controller.Decide().IsLocked };
            case IpcKind.RequestLock:
                _controller.ForceLock(msg.Reason, byParents: false);   // Manipulation: streng, ohne Ausnahme
                return new IpcMessage { Kind = IpcKind.StatusReply, Locked = true };
            case IpcKind.CheckProgram:
            {
                // Schnelle Rückfrage bei Vordergrund-Wechsel: nur Prüfung, keine Installationssuche.
                bool trusted = clientPid > 0 && _controller.IsGenuineAgent(clientPid);
                var ex = _controller.GetExceptionView(msg.ForegroundPid, trusted, msg.LocalAppData, checkInstalled: false);
                return new IpcMessage
                {
                    Kind = IpcKind.StatusReply, Locked = _controller.Decide().IsLocked,
                    Success = ex.ForegroundPid > 0 && ex.ForegroundPid == msg.ForegroundPid,
                    ExceptionForegroundPid = ex.ForegroundPid, ExceptionUncounted = ex.Uncounted
                };
            }
            case IpcKind.LaunchProgram:
            {
                var (ok, path, name) = _controller.GetLaunchPath(msg.LocalAppData);
                return new IpcMessage
                {
                    Kind = IpcKind.StatusReply, Locked = _controller.Decide().IsLocked,
                    Success = ok, ProgramPath = ok ? path : "", ExceptionName = name
                };
            }
            default:
                return new IpcMessage { Kind = IpcKind.StatusReply, Locked = _controller.Decide().IsLocked };
        }
    }
}
