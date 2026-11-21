using System.Runtime.Versioning;
using FrohLock.Core;
using FrohLock.Core.Logging;
using FrohLock.Core.Models;
using FrohLock.Core.Schedule;
using FrohLock.Service.Interop;

namespace FrohLock.Service;

/// <summary>
/// Prüft im DIENST, ob ein laufender Prozess wirklich das erlaubte Ausnahme-Programm ist (Evoluzzer):
/// gleiche Windows-Sitzung wie die Konsole, Dateiname = exe, Prüfsumme passt, Datei nach dem Start nicht
/// ausgetauscht, keine Startparameter, die ein anderes Spiel laden. Der Agent liefert nur die Prozess-Kennung.
/// Ergebnis je Prozess kurz zwischengespeichert; Prüfsummen je Datei (Pfad+Größe+Zeiten) im <see cref="ProgramVerifier"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProgramGuard
{
    private readonly ProgramVerifier _verifier = new(ProcessInfo.ChangeTimeUtc);
    private readonly AuditLog _audit;
    private readonly object _gate = new();
    private readonly Dictionary<int, (DateTime start, AppException? rule, DateTime atUtc, string rulesKey)> _pidCache = new();
    private readonly Dictionary<int, (DateTime start, bool genuine)> _agentCache = new();
    private readonly Dictionary<string, DateTime> _lastAudit = new();

    private static readonly TimeSpan PidCacheFor = TimeSpan.FromSeconds(2);

    public ProgramGuard(AuditLog audit) => _audit = audit;

    private static string RulesKey(IReadOnlyList<AppException> rules)
        => string.Join(";", rules.Select(r => $"{r.Exe}|{r.Mode}|{string.Join(",", r.Sha256 ?? new List<string>())}"));

    /// <summary>Passende (geprüfte) Ausnahme für den Prozess – oder null.</summary>
    public AppException? MatchProcess(int pid, IReadOnlyList<AppException> rules)
    {
        if (pid <= 4 || rules.Count == 0) return null;
        var snap = ProcessInfo.Get(pid);
        if (snap is null) return null;
        uint active = SessionLauncher.ActiveConsoleSessionId;
        if (active == 0xFFFFFFFF || (uint)snap.SessionId != active) return null;

        string key = RulesKey(rules);
        lock (_gate)
        {
            if (_pidCache.TryGetValue(pid, out var c) && c.start == snap.StartUtc && c.rulesKey == key
                && DateTime.UtcNow - c.atUtc < PidCacheFor)
                return c.rule;
        }

        AppException? match = null;
        foreach (var rule in rules)
        {
            if (!ProgramVerifier.NameMatches(snap.ImagePath, rule.Exe)) continue;
            var check = _verifier.Verify(snap.ImagePath, rule, snap.StartUtc);
            if (!check.Ok)
            {
                AuditOnce($"{snap.ImagePath}|{check.Verdict}",
                    $"⚠ „{rule.Name}“ abgelehnt: {Explain(check)} ({snap.ImagePath})");
                continue;
            }
            if (!ProgramVerifier.ArgumentsAllowed(snap.CommandLine))
            {
                AuditOnce($"{snap.ImagePath}|args",
                    $"⚠ „{rule.Name}“ abgelehnt: verdächtige Startparameter ({Short(snap.CommandLine)})");
                continue;
            }
            match = rule;
            break;
        }

        lock (_gate)
        {
            if (_pidCache.Count > 64) _pidCache.Clear();
            _pidCache[pid] = (snap.StartUtc, match, DateTime.UtcNow, key);
        }
        return match;
    }

    /// <summary>Installiertes Programm suchen und prüfen (Benutzer-Installation oder Programme).</summary>
    public ProgramCheck FindInstalled(AppException rule, string? localAppData)
    {
        string? lad = SafeBaseDir(localAppData);
        var candidates = ProgramVerifier.CandidatePaths(rule, lad,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        var found = _verifier.FindInstalled(rule, candidates);
        if (!found.Ok && found.Verdict != ProgramVerdict.NotFound)
            AuditOnce($"{found.Path}|{found.Verdict}|install", $"⚠ „{rule.Name}“ gefunden, aber nicht erlaubt: {Explain(found)} ({found.Path})");
        return found;
    }

    /// <summary>Kommt eine Pipe-Meldung wirklich vom eingebauten Sperrbildschirm (Program Files)?</summary>
    public bool IsGenuineAgent(int clientPid)
    {
        if (clientPid <= 4) return false;
        var snap = ProcessInfo.Get(clientPid);
        if (snap is null) return false;
        lock (_gate)
            if (_agentCache.TryGetValue(clientPid, out var c) && c.start == snap.StartUtc) return c.genuine;
        var expected = Path.Combine(AppPaths.InstallDir, "FrohLockAgent.exe");
        bool genuine = string.Equals(Path.GetFullPath(snap.ImagePath), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_agentCache.Count > 32) _agentCache.Clear();
            _agentCache[clientPid] = (snap.StartUtc, genuine);
        }
        if (!genuine) AuditOnce($"fake|{snap.ImagePath}", $"⚠ Fremdes Programm meldet sich als Sperrbildschirm: {snap.ImagePath} – ignoriert");
        return genuine;
    }

    private static string? SafeBaseDir(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || dir.Length > 240 || dir.Contains("..") || !Path.IsPathFullyQualified(dir))
                return null;
            return dir;
        }
        catch { return null; }
    }

    private static string Explain(ProgramCheck c) => c.Verdict switch
    {
        ProgramVerdict.HashMismatch => "Prüfsumme passt nicht" + (c.Detail.Length > 0 ? $" – {c.Detail}" : ""),
        ProgramVerdict.ChangedAfterStart => "Programmdatei wurde nach dem Start ausgetauscht",
        ProgramVerdict.Unreadable => "Datei nicht lesbar",
        ProgramVerdict.InvalidRule => "Ausnahme unvollständig" + (c.Detail.Length > 0 ? $" – {c.Detail}" : ""),
        ProgramVerdict.NotFound => "nicht gefunden",
        _ => c.Verdict.ToString()
    };

    private static string Short(string? s)
    {
        s ??= "nicht lesbar";
        s = s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 120 ? s[..120] + "…" : s;
    }

    private void AuditOnce(string key, string text)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_lastAudit.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
            if (_lastAudit.Count > 64) _lastAudit.Clear();
            _lastAudit[key] = now;
        }
        try { _audit.Write("AUSNAHME", text); } catch { }
    }
}
