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
/// Named-Pipe-Server. Der Overlay-Agent fragt hier Status ab und reicht PIN-Versuche ein.
/// PIN-Prüfung passiert im Dienst (LocalSystem). Pipe-ACL: nur authentifizierte Nutzer,
/// die Prüfung/Autorisierung erfolgt inhaltlich (kein Vertrauen in den Agent-Prozess).
/// </summary>
public sealed class IpcServer : BackgroundService
{
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
            try
            {
                using var server = CreatePipe();
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleClientAsync(server, stoppingToken); // eine Verbindung nach der anderen ist ok; Overlay pollt seriell
                // Auf Abschluss warten, damit wir eine frische Pipe-Instanz öffnen.
                await DrainAsync(server, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "IPC-Schleife Fehler");
                await Task.Delay(500, stoppingToken).ContinueWith(_ => { }).ConfigureAwait(false);
            }
        }
    }

    private static async Task DrainAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        // Kleiner Puffer, bis die Verbindung geschlossen ist.
        while (server.IsConnected && !ct.IsCancellationRequested)
            await Task.Delay(50, ct).ConfigureAwait(false);
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var pipeSecurity = new PipeSecurity();
        // Authentifizierte Nutzer dürfen verbinden (der Agent läuft im Nutzerkontext),
        // SYSTEM/Administratoren volle Rechte. Inhaltlich vertrauen wir dem Agent nicht.
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            AppPaths.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity);
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return;
            var msg = Json.Deserialize<IpcMessage>(line) ?? new IpcMessage();
            var reply = Handle(msg);
            await writer.WriteLineAsync(Json.Serialize(reply)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPC-Client Fehler");
        }
    }

    private IpcMessage Handle(IpcMessage msg)
    {
        switch (msg.Kind)
        {
            case IpcKind.GetStatus:
            {
                var d = _controller.Decide();
                return new IpcMessage
                {
                    Kind = IpcKind.StatusReply,
                    Locked = d.IsLocked,
                    Reason = d.Reason,
                    MinutesUntilLock = d.IsLocked ? -1 : _controller.MinutesUntilLock()
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
