namespace FrohLock.Core.Crypto;

/// <summary>
/// Fest eingebauter Public Key des Signaturservers (PRODUKTIV).
/// Der zugehoerige private Schluessel liegt AUSSCHLIESSLICH auf dem Server
/// (~/apps/frohlock/secrets/signing_private.pem, dort erzeugt, verlaesst ihn nie).
/// </summary>
public static class EmbeddedKeys
{
    public const string SigningPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAhMBhplCOg5jcy2ORMbR5
cyF08qRgUyJjsxfyYGTqwW6Y61e+hICRk9tczqHmCo9eWMS70BHZrickHsBhl0Ms
31fymtbunk85yxbuif8EwpH84zAk4Dh1ZvkcsMD/J93bBHQ8XAD9gW5YC1pTAdBp
toUSV+wheUoTE3DQoAAji/AOpZcd6RUSpomLdw1YwszpvPyZXvpg3BZNlQFlwdYY
2Exrf33lzqmCOnoaxcNJlv62kpLm4k2Myyyi6pw3v21CAFHuHF/vVWvdCjZjiE/W
KlMEDS3+xhpyh/6lXfuOcv0g08N7IsGz6Vv+qYBS/p++YMzu38Lo5DFX6Q2L96YX
MwIDAQAB
-----END PUBLIC KEY-----";
}
