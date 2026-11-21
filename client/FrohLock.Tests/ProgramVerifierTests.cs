using System.Security.Cryptography;
using System.Text;
using FrohLock.Core.Models;
using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

/// <summary>Erkennen des Ausnahme-Programms (Dateiname + Prüfsumme) mit echten Temp-Dateien.</summary>
public sealed class ProgramVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "frohlock_prog_" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Genuine = Encoding.UTF8.GetBytes("Godot-4.7.2-Laufzeit (Test)");
    private static readonly byte[] Foreign = Encoding.UTF8.GetBytes("Ganz anderes Programm, z. B. ein Browser");

    public ProgramVerifierTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private string Put(string relative, byte[] content)
    {
        var p = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, content);
        return p;
    }

    private static AppException Rule(params string[] hashes)
        => new() { Name = "Evoluzzer", Exe = "Evoluzzer.exe", Mode = "beides", Sha256 = hashes.ToList() };

    [Fact]
    public void Genuine_file_with_matching_hash_is_ok()
    {
        var p = Put("Programs/Evoluzzer/Evoluzzer.exe", Genuine);
        var v = new ProgramVerifier();
        Assert.Equal(ProgramVerdict.Ok, v.Verify(p, Rule(Sha(Genuine))).Verdict);
        // Groß-/Kleinschreibung und Leerzeichen in der hinterlegten Prüfsumme sind egal.
        Assert.True(v.Verify(p, Rule(" " + Sha(Genuine).ToUpperInvariant() + " ")).Ok);
        // Dateiname Groß/Klein egal.
        var p2 = Put("x/EVOLUZZER.EXE", Genuine);
        Assert.True(v.Verify(p2, Rule(Sha(Genuine))).Ok);
    }

    [Fact]
    public void Renamed_foreign_program_is_rejected_by_hash()
    {
        var p = Put("Programs/Evoluzzer/Evoluzzer.exe", Foreign);   // z. B. umbenannter Browser
        var c = new ProgramVerifier().Verify(p, Rule(Sha(Genuine)));
        Assert.Equal(ProgramVerdict.HashMismatch, c.Verdict);
        Assert.False(c.Ok);
    }

    [Fact]
    public void Wrong_hash_is_rejected_and_other_name_is_rejected()
    {
        var p = Put("Programs/Evoluzzer/Evoluzzer.exe", Genuine);
        Assert.Equal(ProgramVerdict.HashMismatch, new ProgramVerifier().Verify(p, Rule(new string('a', 64))).Verdict);
        // Echte Datei, aber unter anderem Namen (z. B. Godot.exe) -> nicht die Ausnahme.
        var other = Put("Programs/Godot/Godot.exe", Genuine);
        Assert.Equal(ProgramVerdict.NameMismatch, new ProgramVerifier().Verify(other, Rule(Sha(Genuine))).Verdict);
        // Windows-Pfad mit Backslash: nur der Dateiname zählt.
        Assert.True(ProgramVerifier.NameMatches(@"C:\Users\renat\AppData\Local\Programs\Evoluzzer\Evoluzzer.exe", "evoluzzer.exe"));
        Assert.False(ProgramVerifier.NameMatches(@"C:\x\Evoluzzer.exe.bak", "Evoluzzer.exe"));
        Assert.False(ProgramVerifier.NameMatches(@"C:\x\Evoluzzer.exe", ""));
    }

    [Fact]
    public void Without_hashes_only_the_name_counts_but_invalid_hashes_block()
    {
        var p = Put("Programs/Evoluzzer/Evoluzzer.exe", Foreign);
        var v = new ProgramVerifier();
        Assert.True(v.Verify(p, Rule()).Ok);                                   // keine Prüfsumme hinterlegt
        Assert.Equal(ProgramVerdict.InvalidRule, v.Verify(p, Rule("kaputt")).Verdict);   // nur ungültige -> nie
        Assert.Equal(ProgramVerdict.InvalidRule, v.Verify(p, new AppException { Exe = "", Mode = "beides" }).Verdict);
        Assert.Equal(ProgramVerdict.InvalidRule, v.Verify(p, null).Verdict);
    }

    [Fact]
    public void Missing_or_unreadable_path_is_not_ok()
    {
        var v = new ProgramVerifier();
        Assert.Equal(ProgramVerdict.NotFound, v.Verify(Path.Combine(_root, "fehlt", "Evoluzzer.exe"), Rule(Sha(Genuine))).Verdict);
        Assert.False(v.Verify(null, Rule(Sha(Genuine))).Ok);
        Assert.False(v.Verify("", Rule(Sha(Genuine))).Ok);
        // Ordner statt Datei
        Directory.CreateDirectory(Path.Combine(_root, "dir", "Evoluzzer.exe"));
        Assert.False(v.Verify(Path.Combine(_root, "dir", "Evoluzzer.exe"), Rule(Sha(Genuine))).Ok);
    }

    [Fact]
    public void Hash_is_cached_per_path_size_and_time_and_recomputed_after_change()
    {
        var p = Put("Programs/Evoluzzer/Evoluzzer.exe", Genuine);
        var v = new ProgramVerifier();
        var rule = Rule(Sha(Genuine));
        Assert.True(v.Verify(p, rule).Ok);
        Assert.True(v.Verify(p, rule).Ok);
        Assert.Equal(1, v.HashComputations);

        // Kind ersetzt die Datei durch ein anderes Programm -> neue Prüfung, abgelehnt.
        File.WriteAllBytes(p, Foreign);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(ProgramVerdict.HashMismatch, v.Verify(p, rule).Verdict);
        Assert.Equal(2, v.HashComputations);
    }

    [Fact]
    public void File_changed_after_process_start_is_rejected()
    {
        var p = Put("Programs/Evoluzzer/Evoluzzer.exe", Genuine);
        var rule = Rule(Sha(Genuine));
        var fileTime = File.GetLastWriteTimeUtc(p);
        // Prozess lief schon vor der Datei -> die Datei gehört nicht sicher zu ihm (Austausch-Trick).
        Assert.Equal(ProgramVerdict.ChangedAfterStart, new ProgramVerifier().Verify(p, rule, fileTime.AddMinutes(-10)).Verdict);
        // Prozess startete nach der Datei -> ok.
        Assert.True(new ProgramVerifier().Verify(p, rule, fileTime.AddMinutes(1)).Ok);
        // Windows-ChangeTime (Umbenennen/Verschieben) nach dem Start -> abgelehnt.
        var v = new ProgramVerifier(_ => DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(ProgramVerdict.ChangedAfterStart, v.Verify(p, rule, DateTime.UtcNow).Verdict);
        // ChangeTime nicht lesbar (Ausnahme) -> nur die normalen Zeiten zählen.
        var broken = new ProgramVerifier(_ => throw new IOException());
        Assert.True(broken.Verify(p, rule, fileTime.AddMinutes(1)).Ok);
    }

    [Fact]
    public void Find_installed_in_user_programs_folder()
    {
        var lad = Path.Combine(_root, "LocalAppData");
        var rule = Rule(Sha(Genuine));
        var v = new ProgramVerifier();
        var cands = ProgramVerifier.CandidatePaths(rule, lad, Path.Combine(_root, "PF"), null);
        Assert.Contains(Path.Combine(lad, "Programs", "Evoluzzer", "Evoluzzer.exe"), cands);

        Assert.Equal(ProgramVerdict.NotFound, v.FindInstalled(rule, cands).Verdict);   // noch nicht installiert

        Put(Path.Combine("LocalAppData", "Programs", "Evoluzzer", "Evoluzzer.exe"), Foreign);
        Assert.Equal(ProgramVerdict.HashMismatch, v.FindInstalled(rule, cands).Verdict); // falsches Programm dort

        File.WriteAllBytes(Path.Combine(lad, "Programs", "Evoluzzer", "Evoluzzer.exe"), Genuine);
        File.SetLastWriteTimeUtc(Path.Combine(lad, "Programs", "Evoluzzer", "Evoluzzer.exe"), DateTime.UtcNow.AddSeconds(5));
        var found = v.FindInstalled(rule, cands);
        Assert.True(found.Ok);
        Assert.EndsWith("Evoluzzer.exe", found.Path);
    }

    [Fact]
    public void Candidate_paths_ignore_dangerous_names()
    {
        var rule = new AppException { Name = "..\\..\\Windows", Exe = "Evoluzzer.exe", Mode = "beides" };
        var c = ProgramVerifier.CandidatePaths(rule, Path.Combine(_root, "L"), null, null);
        Assert.All(c, p => Assert.DoesNotContain("..", p));
        Assert.Empty(ProgramVerifier.CandidatePaths(new AppException { Exe = "kein-exe" }, "/x", "/y", "/z"));
    }

    [Theory]
    [InlineData("\"C:\\Users\\renat\\AppData\\Local\\Programs\\Evoluzzer\\Evoluzzer.exe\"", true)]
    [InlineData("\"C:\\Users\\renat\\AppData\\Local\\Programs\\Evoluzzer\\Evoluzzer.exe\" ", true)]
    [InlineData("C:\\Spiele\\Evoluzzer.exe --fullscreen", true)]
    [InlineData("\"C:\\Program Files\\Evoluzzer\\Evoluzzer.exe\" --rendering-driver opengl3", true)]
    [InlineData("Evoluzzer.exe --main-pack C:\\x\\anderes.pck", false)]
    [InlineData("Evoluzzer.exe --main-pack=C:\\x\\anderes", false)]
    [InlineData("Evoluzzer.exe --path C:\\x\\projekt", false)]
    [InlineData("Evoluzzer.exe C:\\x\\anderes.pck", false)]
    [InlineData("Evoluzzer.exe \"C:\\mein ordner\\project.godot\"", false)]
    [InlineData("Evoluzzer.exe -s C:\\x\\hack.gd", false)]
    [InlineData("Evoluzzer.exe --editor", false)]
    [InlineData("Evoluzzer.exe res://andere_szene.tscn", false)]
    public void Godot_arguments_that_load_other_games_are_rejected(string cmd, bool allowed)
        => Assert.Equal(allowed, ProgramVerifier.ArgumentsAllowed(cmd));

    [Fact]
    public void Unreadable_command_line_is_rejected()
        => Assert.False(ProgramVerifier.ArgumentsAllowed(null));
}
