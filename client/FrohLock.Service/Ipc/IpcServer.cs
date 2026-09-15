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
                var reply = Handle(msg);
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

    private IpcMessage Handle(IpcMessage msg)
    {
        switch (msg.Kind)
        {
            case IpcKind.GetStatus:
            {
                var d = _controller.Decide();
                var baseUrl = _controller.Config?.ServerBaseUrl ?? "";
                return new IpcMessage
                {
                    Kind = IpcKind.StatusReply,
                    Locked = d.IsLocked,
                    Reason = d.Reason,
                    MinutesUntilLock = d.IsLocked ? -1 : _controller.MinutesUntilLock(),
                    ForgotUrl = string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl.TrimEnd('/') + "/forgot",
                    ScheduleSummary = _controller.TodayScheduleSummary(),
                    UsageMinutes = _controller.TodayUsageMinutes(),
                    BudgetMinutes = _controller.TodayBudgetMinutes(),
                    Configured = _controller.IsConfigured
                };
            }
            case IpcKind.SubmitPin:
            {
                var (ok, remaining) = _controller.SubmitPin(msg.Pin ?? "");
                return new IpcMessage { Kind = IpcKind.PinResult, Success = ok, PinFailuresRemaining = remaining };
            }
            case IpcKind.AgentAlive:
                _onAgentAlive();
                return new IpcMessage { Kind = IpcKind.StatusReply, Locked = _controller.Decide().IsLocked };
            case IpcKind.RequestLock:
                _controller.ForceLock(msg.Reason);
                return new IpcMessage { Kind = IpcKind.StatusReply, Locked = true };
            default:
                return new IpcMessage { Kind = IpcKind.StatusReply, Locked = _controller.Decide().IsLocked };
        }
    }
}
