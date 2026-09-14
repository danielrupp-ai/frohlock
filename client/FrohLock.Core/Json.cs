using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrohLock.Core;

/// <summary>Zentrale JSON-Optionen, damit Signatur-Kanonik überall identisch ist.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // Für signierte Nutzlast: stabile, kompakte Serialisierung (Property-Reihenfolge = Deklaration).
    public static byte[] Canonical<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string s) => JsonSerializer.Deserialize<T>(s, Options);
    public static T? Deserialize<T>(ReadOnlySpan<byte> s) => JsonSerializer.Deserialize<T>(s, Options);
}
