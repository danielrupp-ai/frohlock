namespace FrohLock.Core.Models;

/// <summary>
/// Eine Programm-Ausnahme aus der signierten Konfiguration (Windows, ab 0.31.0).
/// Bewusst nur Zeichenketten: ein unbekannter Wert darf die Konfiguration nie unlesbar machen –
/// er wirkt einfach nicht (<see cref="Schedule.AppExceptionPolicy"/>).
/// </summary>
public sealed class AppException
{
    /// <summary>Anzeigename, z. B. „Evoluzzer“.</summary>
    public string Name { get; set; } = "";

    /// <summary>Dateiname der Programmdatei, z. B. „Evoluzzer.exe“ (ohne Pfad, Groß/Klein egal).</summary>
    public string Exe { get; set; } = "";

    /// <summary>
    /// Erlaubte SHA-256-Prüfsummen der Programmdatei (hex). Leer = nur der Dateiname zählt.
    /// Der Installationsort liegt im Benutzerordner (vom Kind beschreibbar) – die Prüfsumme ist der Schutz
    /// gegen ein fremdes, umbenanntes Programm.
    /// </summary>
    public List<string>? Sha256 { get; set; } = new();

    /// <summary>„offen“ (wirkt nicht), „sperre“ (auch in der Sperre), „zeit“ (zählt nicht), „beides“.</summary>
    public string Mode { get; set; } = "offen";
}
