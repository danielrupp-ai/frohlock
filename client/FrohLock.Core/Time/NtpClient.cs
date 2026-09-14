using System.Net;
using System.Net.Sockets;

namespace FrohLock.Core.Time;

/// <summary>Minimaler SNTP-Client (RFC 4330) über UDP/123.</summary>
public static class NtpClient
{
    private static readonly DateTime Epoch1900 = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static async Task<DateTime?> QueryAsync(string host, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var data = new byte[48];
            data[0] = 0x1B; // LI=0, VN=3, Mode=3 (client)

            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            udp.Client.SendTimeout = (int)timeout.TotalMilliseconds;

            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            if (addresses.Length == 0) return null;
            var ep = new IPEndPoint(addresses[0], 123);

            await udp.SendAsync(data, data.Length, ep).WaitAsync(timeout, ct).ConfigureAwait(false);
            var result = await udp.ReceiveAsync(ct).AsTask().WaitAsync(timeout, ct).ConfigureAwait(false);
            var buf = result.Buffer;
            if (buf.Length < 48) return null;

            // Transmit Timestamp: Bytes 40..47 (Sekunden + Bruch, big-endian).
            ulong intPart = ((ulong)buf[40] << 24) | ((ulong)buf[41] << 16) | ((ulong)buf[42] << 8) | buf[43];
            ulong fracPart = ((ulong)buf[44] << 24) | ((ulong)buf[45] << 16) | ((ulong)buf[46] << 8) | buf[47];
            if (intPart == 0) return null;

            double ms = intPart * 1000.0 + fracPart * 1000.0 / 0x100000000L;
            return Epoch1900.AddMilliseconds(ms);
        }
        catch
        {
            return null;
        }
    }
}
