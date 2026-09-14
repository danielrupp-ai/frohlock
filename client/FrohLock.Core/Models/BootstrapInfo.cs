namespace FrohLock.Core.Models;

/// <summary>
/// Minimale, unkritische Kopplungsdaten, die das Setup lokal ablegt, damit der Dienst
/// den Server erreichen kann, BEVOR die erste signierte Config vorliegt.
/// Enthält KEIN Geheimnis (das Geräte-Token liegt separat in device.token).
/// </summary>
public sealed class BootstrapInfo
{
    public string ServerBaseUrl { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public List<string> TlsSpkiPins { get; set; } = new();
}
