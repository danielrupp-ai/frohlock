namespace FrohLock.Core;

/// <summary>
/// Vorbelegungen für die Einrichtung. Beim Release ggf. anpassen (oder in der CI setzen).
/// Der Elternteil kann die Server-Adresse im Setup weiterhin überschreiben.
/// </summary>
public static class Branding
{
    /// <summary>Vorbelegte Server-Adresse der Brücke (im Setup vorausgefüllt).</summary>
    public const string DefaultServerBaseUrl = "https://frohlock.froehlichdienste.de";

    // ---- Master-/Notfall-PIN ----
    // Entsperrt IMMER (auch ohne Konfiguration, auch bei Fehlbedienungs-Sperre).
    // Nur der PBKDF2-Hash steht hier – die eigentliche PIN kennt nur der Elternteil.
    // Aktuell: "FrohLock-Eltern-2531" (bei Bedarf neu erzeugen + Client neu bauen).
    public const string MasterUnlockHash = "4niEsTR/uwcpdSkYNAPvq9sOYFw2iFQUE1OkN7GEsJM=";
    public const string MasterUnlockSalt = "gh3nKrbedEjnc7EvZx9Xdg==";
    public const int MasterUnlockIterations = 210_000;

    // Zweite, REIN NUMERISCHE Master-PIN (Ziffern liegen bei jeder Tastaturbelegung gleich).
    // Aktuell: "49258137".
    public const string MasterUnlockHash2 = "3W8tJJ+3LLmin6RmCJLzOy9wknJLOrSCkRIFPill2vs=";
    public const string MasterUnlockSalt2 = "STcnhS506Krp4V2BfGCY7Q==";

    /// <summary>Prüft eine PIN gegen ALLE Master-PINs (Buchstaben + numerisch).</summary>
    public static bool IsMasterPin(string pin, Func<string, string, string, int, bool> verify)
        => verify(pin, MasterUnlockHash, MasterUnlockSalt, MasterUnlockIterations)
           || verify(pin, MasterUnlockHash2, MasterUnlockSalt2, MasterUnlockIterations);
}
