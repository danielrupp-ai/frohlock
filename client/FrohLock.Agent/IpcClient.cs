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

    public Task<IpcMessage?> SubmitPinAsync(string pin, CancellationToken ct = default)
        => SendAsync(new IpcMessage { Kind = IpcKind.SubmitPin, Pin = pin }, ct: ct);

    public Task<IpcMessage?> AliveAsync(CancellationToken ct = default)
        => SendAsync(new IpcMessage { Kind = IpcKind.AgentAlive }, ct: ct);
}
