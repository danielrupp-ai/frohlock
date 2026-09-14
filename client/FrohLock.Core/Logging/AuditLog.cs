using System.Text;

namespace FrohLock.Core.Logging;

/// <summary>
/// Schlankes, thread-sicheres Append-Log für sicherheitsrelevante Ereignisse
/// (Sperren/Entsperren, PIN-Fehlversuche, Manipulationsversuche, Befehle).
/// Wird zusätzlich zum Server gespiegelt. Rotiert bei Überschreitung.
/// </summary>
public sealed class AuditLog
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();

    public AuditLog(string path, long maxBytes = 2_000_000)
    {
        _path = path;
        _maxBytes = maxBytes;
    }

    public void Write(string category, string message)
    {
        try
        {
            lock (_gate)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                RotateIfNeeded();
                var line = $"{DateTime.UtcNow:o}\t{category}\t{message.Replace('\n', ' ').Replace('\t', ' ')}\n";
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
        }
        catch { /* Logging darf nie den Betrieb stören */ }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > _maxBytes)
            {
                var bak = _path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(_path, bak);
            }
        }
        catch { }
    }

    /// <summary>Liest die letzten n Zeilen (für Heartbeat-Mitspiegeln).</summary>
    public IReadOnlyList<string> Tail(int n)
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path)) return Array.Empty<string>();
                var all = File.ReadAllLines(_path);
                return all.Length <= n ? all : all[^n..];
            }
        }
        catch { return Array.Empty<string>(); }
    }
}
