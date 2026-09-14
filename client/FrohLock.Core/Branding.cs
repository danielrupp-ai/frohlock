namespace FrohLock.Core;

/// <summary>
/// Vorbelegungen für die Einrichtung. Beim Release ggf. anpassen (oder in der CI setzen).
/// Der Elternteil kann die Server-Adresse im Setup weiterhin überschreiben.
/// </summary>
public static class Branding
{
    /// <summary>Vorbelegte Server-Adresse der Brücke (im Setup vorausgefüllt).</summary>
    public const string DefaultServerBaseUrl = "https://frohlock.froehlichdienste.de";
}
