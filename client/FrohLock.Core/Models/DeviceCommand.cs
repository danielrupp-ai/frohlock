namespace FrohLock.Core.Models;

public enum CommandType
{
    Unlock,        // temporär entsperren
    Lock,          // sofort sperren
    UpdateConfig,  // neue signierte Config anwenden
    ResetPin,      // neuen PIN-Hash setzen (in Payload)
    Uninstall,     // Admin-Wipe: saubere Deinstallation ohne PIN
    Update,        // Selbst-Update anstoßen
    Ping
}

/// <summary>Ein vom Server signierter Fernbefehl. Ohne gültige Signatur wird er verworfen.</summary>
public sealed class DeviceCommand
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; set; } = "";
    public CommandType Type { get; set; }

    /// <summary>Ausgabezeitpunkt (Unix-Sekunden, Serverzeit) — gegen Replay via Ablauf geprüft.</summary>
    public long IssuedAtUnix { get; set; }

    /// <summary>Gültig bis (Unix-Sekunden). 0 = kein Ablauf.</summary>
    public long ExpiresAtUnix { get; set; }

    /// <summary>Bei Unlock: Dauer der Entsperrung in Minuten.</summary>
    public int? UnlockMinutes { get; set; }

    /// <summary>Freies Nutzlast-Feld (z. B. neuer PinHash/Salt, Update-URL).</summary>
    public Dictionary<string, string>? Payload { get; set; }
}

/// <summary>Signierter Umschlag: Base64-Signatur über die kanonische JSON-Nutzlast.</summary>
public sealed class SignedEnvelope
{
    public string PayloadBase64 { get; set; } = "";
    public string SignatureBase64 { get; set; } = "";
    public string Algorithm { get; set; } = "RSASSA-PSS-SHA256";
}
