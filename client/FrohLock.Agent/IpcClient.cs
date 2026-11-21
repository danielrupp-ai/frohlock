using System.IO;
using System.IO.Pipes;
using System.Text;
using FrohLock.Core;
using FrohLock.Core.Ipc;

namespace FrohLock.Agent;

/// <summary>Client zum Dienst-Pipe. Eine kurze Verbindung pro Anfrage (Overlay pollt seriell).</summary>
public sealed class IpcClient
{
    public async Task<IpcMessage?> SendAsync(IpcMessage msg, int timeoutMs = 3000, CancellationToken ct = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

            await writer.WriteLineAsync(Json.Serialize(msg)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            return line is null ? null : Json.Deserialize<IpcMessage>(line);
        }
        catch
        {
            return null;
        }
    }

    public Task<IpcMessage?> GetStatusAsync(CancellationToken ct = default)
        => SendAsync(new IpcMessage { Kind = IpcKind.GetStatus }, ct: ct);

    /// <summary>Status + Vordergrund-Meldung des Sperrbildschirms (welcher Prozess ist vorn? – geprüft wird im Dienst).</summary>
    public Task<IpcMessage?> GetStatusAsync(int foregroundPid, string localAppData, CancellationToken ct = default)
        => SendAsync(new IpcMessage
        {
            Kind = IpcKind.GetStatus, ForegroundPid = foregroundPid, ReportsForeground = true, LocalAppData = localAppData
        }, ct: ct);

    /// <summary>Ist dieser Prozess das geprüfte Ausnahme-Programm und gerade erlaubt?</summary>
    public Task<IpcMessage?> CheckProgramAsync(int pid, CancellationToken ct = default)
        => SendAsync(new IpcMessage { Kind = IpcKind.CheckProgram, ForegroundPid = pid }, ct: ct);

    /// <summary>Geprüften Pfad des Ausnahme-Programms holen (nur in einer Sperre, die es erlaubt).</summary>
    public Task<IpcMessage?> LaunchProgramAsync(string localAppData, CancellationToken ct = default)
        => SendAsync(new IpcMessage { Kind = IpcKind.LaunchProgram, LocalAppData = localAppData }, timeoutMs: 5000, ct: ct);

    public Task<IpcMessage?> SubmitPinAsync(string pin, CancellationToken ct = default)
        => SendAsync(new IpcMessage { Kind = IpcKind.SubmitPin, Pin = pin }, ct: ct);

    public Task<IpcMessage?> AliveAsync(bool displayOn, bool lidOpen, bool externalDisplay, bool sessionLocked,
        List<FrohLock.Core.Schedule.MinuteRange>? screenOnMinutes = null, bool lockVisible = false,
        bool foregroundOurs = false, CancellationToken ct = default)
        => SendAsync(new IpcMessage
        {
            Kind = IpcKind.AgentAlive, DisplayOn = displayOn, LidOpen = lidOpen,
            ExternalDisplay = externalDisplay, SessionLocked = sessionLocked, ScreenOnMinutes = screenOnMinutes,
            SessionId = SessionIdOfThisProcess, LockVisible = lockVisible, ForegroundOurs = foregroundOurs
        }, ct: ct);

    private static readonly int SessionIdOfThisProcess = SafeSessionId();
    private static int SafeSessionId()
    {
        try { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.SessionId; } catch { return -1; }
    }
}
