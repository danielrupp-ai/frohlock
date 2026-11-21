using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FrohLock.Core.Models;

namespace FrohLock.Core.Schedule;

/// <summary>Ergebnis der Prüfung einer Programmdatei.</summary>
public enum ProgramVerdict
{
    Ok,
    /// <summary>Dateiname passt nicht zur Ausnahme.</summary>
    NameMismatch,
    /// <summary>Prüfsumme steht nicht in der erlaubten Liste (z. B. umbenanntes fremdes Programm).</summary>
    HashMismatch,
    /// <summary>Datei fehlt.</summary>
    NotFound,
    /// <summary>Datei/Pfad nicht lesbar.</summary>
    Unreadable,
    /// <summary>Datei wurde nach dem Programmstart verändert/ersetzt (Austausch-Trick).</summary>
    ChangedAfterStart,
    /// <summary>Verdächtige Startparameter (z. B. anderes Spielpaket über --main-pack).</summary>
    ForbiddenArguments,
    /// <summary>Ausnahme unvollständig (keine/ungültige Programmdatei, nur ungültige Prüfsummen).</summary>
    InvalidRule
}

public sealed record ProgramCheck(ProgramVerdict Verdict, string Path, string Detail = "")
{
    public bool Ok => Verdict == ProgramVerdict.Ok;
}

/// <summary>
/// Prüft, ob eine Programmdatei wirklich das erlaubte Ausnahme-Programm ist: Dateiname = <c>exe</c> UND –
/// falls Prüfsummen hinterlegt sind – SHA-256 der Datei steht in der Liste. Das Ergebnis wird je
/// Pfad + Größe + Änderungs-/Erstellzeit zwischengespeichert (die Godot-Laufzeit ist ~100 MB groß).
/// Läuft im DIENST (LocalSystem) – der Sperrbildschirm meldet nur, welches Fenster vorn ist.
/// Jeder Fehler ergibt „nicht ok“ (im Zweifel gesperrt).
/// </summary>
public sealed class ProgramVerifier
{
    private static readonly Regex ExeRe = new(@"^[A-Za-z0-9][A-Za-z0-9 _.\-]{0,78}\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HexRe = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _hashCache = new(StringComparer.Ordinal);
    private readonly Func<string, DateTime?>? _changeTimeUtc;

    /// <summary>Wie oft wirklich gehasht wurde (für Tests: Cache greift).</summary>
    public int HashComputations { get; private set; }

    /// <param name="changeTimeUtc">
    /// Optional (Windows): NTFS-„ChangeTime“ der Datei – ändert sich auch beim Umbenennen/Verschieben.
    /// Damit fällt auf, wenn nach dem Start eines fremden Programms die echte Datei an seinen Pfad geschoben wird.
    /// </param>
    public ProgramVerifier(Func<string, DateTime?>? changeTimeUtc = null) => _changeTimeUtc = changeTimeUtc;

    public static bool IsValidExeName(string? exe) => !string.IsNullOrWhiteSpace(exe) && ExeRe.IsMatch(exe.Trim());

    /// <summary>Dateiname eines Pfads – Windows- (\) und Unix-Trenner (/).</summary>
    public static string FileNameOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return i >= 0 ? path[(i + 1)..] : path;
    }

    public static bool NameMatches(string? imagePath, string? exe)
        => IsValidExeName(exe) && string.Equals(FileNameOf(imagePath), exe!.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Gültige Prüfsummen (klein, ohne Leerzeichen) + ob überhaupt etwas angegeben war.</summary>
    public static (HashSet<string> valid, bool anyGiven) NormalizeHashes(IEnumerable<string?>? hashes)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        bool any = false;
        foreach (var h in hashes ?? Array.Empty<string?>())
        {
            if (string.IsNullOrWhiteSpace(h)) continue;
            any = true;
            var n = new string(h.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
            if (HexRe.IsMatch(n)) set.Add(n);
        }
        return (set, any);
    }

    /// <summary>
    /// Prüft die Datei an <paramref name="path"/> gegen die Ausnahme.
    /// </summary>
    /// <param name="processStartUtc">Startzeit des laufenden Prozesses (wenn bekannt): ist die Datei danach
    /// erstellt/geändert/umbenannt worden, gehört sie nicht sicher zu diesem Prozess -> abgelehnt.</param>
    public ProgramCheck Verify(string? path, AppException? rule, DateTime? processStartUtc = null)
    {
        path ??= "";
        try
        {
            if (rule is null || !IsValidExeName(rule.Exe)) return new(ProgramVerdict.InvalidRule, path, "keine gültige Programmdatei");
            if (!NameMatches(path, rule.Exe)) return new(ProgramVerdict.NameMismatch, path);
            var (valid, anyGiven) = NormalizeHashes(rule.Sha256);
            if (anyGiven && valid.Count == 0) return new(ProgramVerdict.InvalidRule, path, "nur ungültige Prüfsummen hinterlegt");

            var fi = new FileInfo(path);
            if (!fi.Exists) return new(ProgramVerdict.NotFound, path);
            if ((fi.Attributes & FileAttributes.Directory) != 0) return new(ProgramVerdict.NotFound, path);

            DateTime created = fi.CreationTimeUtc, written = fi.LastWriteTimeUtc;
            if (processStartUtc is { } start)
            {
                var tolerance = TimeSpan.FromSeconds(2);
                DateTime? changed = null;
                try { changed = _changeTimeUtc?.Invoke(path); } catch { changed = null; }
                var latest = new[] { created, written, changed ?? DateTime.MinValue }.Max();
                if (latest > start.ToUniversalTime() + tolerance)
                    return new(ProgramVerdict.ChangedAfterStart, path,
                        $"Datei {latest:HH:mm:ss} verändert, Programm lief seit {start.ToUniversalTime():HH:mm:ss} (UTC)");
            }

            if (valid.Count == 0) return new(ProgramVerdict.Ok, path, "ohne Prüfsumme (nur Dateiname)");

            string key = $"{path.ToLowerInvariant()}|{fi.Length}|{written.Ticks}|{created.Ticks}";
            string? hash;
            lock (_gate) _hashCache.TryGetValue(key, out hash);
            if (hash is null)
            {
                hash = ComputeSha256(path);
                lock (_gate)
                {
                    HashComputations++;
                    if (_hashCache.Count > 64) _hashCache.Clear();
                    _hashCache[key] = hash;
                }
            }
            return valid.Contains(hash)
                ? new(ProgramVerdict.Ok, path)
                : new(ProgramVerdict.HashMismatch, path, $"SHA-256 {hash[..12]}… nicht erlaubt");
        }
        catch (Exception ex)
        {
            return new(ProgramVerdict.Unreadable, path, ex.GetType().Name);
        }
    }

    public static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 16, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Mögliche Installationsorte (fester Satz, keine Eingabe des Kindes): Benutzer-Installation
    /// (%LOCALAPPDATA%\Programs\&lt;Name&gt;\&lt;exe&gt;) und klassisch unter Programme.
    /// </summary>
    public static List<string> CandidatePaths(AppException rule, string? localAppData, string? programFiles, string? programFilesX86)
    {
        var list = new List<string>();
        if (!IsValidExeName(rule.Exe)) return list;
        string exe = rule.Exe.Trim();
        var folders = new List<string>();
        void AddFolder(string? f)
        {
            f = (f ?? "").Trim();
            if (f.Length == 0 || f.Length > 80 || f.Contains("..") || f.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || f.Contains('\\') || f.Contains('/')) return;
            if (!folders.Contains(f, StringComparer.OrdinalIgnoreCase)) folders.Add(f);
        }
        AddFolder(rule.Name);
        AddFolder(exe[..^4]);

        foreach (var f in folders)
        {
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                list.Add(Path.Combine(localAppData, "Programs", f, exe));
                list.Add(Path.Combine(localAppData, f, exe));
            }
            if (!string.IsNullOrWhiteSpace(programFiles)) list.Add(Path.Combine(programFiles, f, exe));
            if (!string.IsNullOrWhiteSpace(programFilesX86)) list.Add(Path.Combine(programFilesX86, f, exe));
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Erster Installationsort, dessen Datei die Prüfung besteht – sonst der „beste“ Fehlbefund.</summary>
    public ProgramCheck FindInstalled(AppException rule, IEnumerable<string> candidates)
    {
        ProgramCheck? best = null;
        foreach (var p in candidates)
        {
            var c = Verify(p, rule);
            if (c.Ok) return c;
            // Ein gefundener, aber falscher Stand ist aussagekräftiger als „fehlt“.
            if (best is null || (best.Verdict == ProgramVerdict.NotFound && c.Verdict != ProgramVerdict.NotFound)) best = c;
        }
        return best ?? new ProgramCheck(ProgramVerdict.NotFound, "");
    }

    // Godot-Startparameter, mit denen sich ein ANDERES Spiel/Projekt oder der Editor starten ließe.
    private static readonly HashSet<string> ForbiddenFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--main-pack", "--path", "--upwards", "-s", "--script", "-e", "--editor", "-p", "--project-manager",
        "--remote-debug", "--debug-server", "--import", "--export-release", "--export-debug", "--export-pack",
        "--doctool", "--build-solutions", "--dump-extension-api", "--dump-gdextension-interface",
        "--recovery-mode", "--debug", "-d", "--test", "--gdscript-docs", "--headless"
    };

    private static readonly string[] ForbiddenExtensions =
        { ".pck", ".zip", ".gd", ".tscn", ".scn", ".godot", ".res", ".tres", ".cfg", ".exe", ".dll", ".bat", ".cmd", ".ps1" };

    /// <summary>
    /// Startparameter erlaubt? Die Programmdatei ist die allgemeine Godot-Laufzeit – über Parameter ließe sie
    /// ein beliebiges anderes Spiel laden. Erlaubt ist darum nichts, was ein Paket/Projekt/Skript nachlädt.
    /// </summary>
    public static bool ArgumentsAllowed(string? commandLine)
    {
        if (commandLine is null) return false;   // nicht lesbar -> im Zweifel nein
        var tokens = SplitCommandLine(commandLine);
        foreach (var raw in tokens.Skip(1))
        {
            var t = raw.Trim().Trim('"').Trim();
            if (t.Length == 0) continue;
            int eq = t.IndexOf('=');
            var flag = eq > 0 ? t[..eq] : t;
            if (ForbiddenFlags.Contains(flag)) return false;
            var lower = t.ToLowerInvariant();
            if (ForbiddenExtensions.Any(x => lower.EndsWith(x, StringComparison.Ordinal))) return false;
            if (lower.Contains("res://") || lower.Contains("user://")) return false;
        }
        return true;
    }

    /// <summary>Einfaches Zerlegen einer Windows-Befehlszeile (Anführungszeichen beachten).</summary>
    public static List<string> SplitCommandLine(string cmd)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in cmd)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any || cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c);
        }
        if (any || cur.Length > 0) list.Add(cur.ToString());
        return list;
    }
}
