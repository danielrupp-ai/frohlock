import CommonCrypto
import Foundation
import Security

/// PBKDF2-HMAC-SHA256 über die UTF-8-Bytes der PIN – identisch zu .NET, Python und Android.
public enum PinHasher {
    public static let defaultIterations = 210_000

    public static func pbkdf2(_ pin: String, salt: Data, iterations: Int, length: Int) -> Data? {
        let pw = Array(pin.utf8)
        guard !pw.isEmpty else { return nil }
        var out = [UInt8](repeating: 0, count: length)
        let rc = salt.withUnsafeBytes { s -> Int32 in
            pw.withUnsafeBufferPointer { p in
                p.baseAddress!.withMemoryRebound(to: Int8.self, capacity: pw.count) { pp in
                    CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), pp, pw.count,
                                         s.bindMemory(to: UInt8.self).baseAddress, salt.count,
                                         CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations), &out, length)
                }
            }
        }
        return rc == kCCSuccess ? Data(out) : nil
    }

    public static func hash(_ pin: String, iterations: Int = defaultIterations) -> (hash: String, salt: String)? {
        var salt = Data(count: 16)
        let ok = salt.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) } == errSecSuccess
        guard ok, let h = pbkdf2(pin, salt: salt, iterations: iterations, length: 32) else { return nil }
        return (h.base64EncodedString(), salt.base64EncodedString())
    }

    public static func verify(_ pin: String, hash: String, salt: String, iterations: Int = defaultIterations) -> Bool {
        guard !pin.isEmpty, let s = Data(base64Encoded: salt), let expected = Data(base64Encoded: hash), !expected.isEmpty,
              let actual = pbkdf2(pin, salt: s, iterations: iterations, length: expected.count) else { return false }
        // Zeitkonstant vergleichen
        var diff: UInt8 = 0
        for (a, b) in zip(actual, expected) { diff |= a ^ b }
        return diff == 0
    }
}

/// Master-/Notfall-PINs: dieselben Hashes wie Branding.cs (Windows) und Android.
public enum MasterPin {
    static let entries = [("4niEsTR/uwcpdSkYNAPvq9sOYFw2iFQUE1OkN7GEsJM=", "gh3nKrbedEjnc7EvZx9Xdg=="),
                          ("3W8tJJ+3LLmin6RmCJLzOy9wknJLOrSCkRIFPill2vs=", "STcnhS506Krp4V2BfGCY7Q==")]

    public static func isMaster(_ pin: String) -> Bool {
        entries.contains { PinHasher.verify(pin, hash: $0.0, salt: $0.1, iterations: 210_000) }
    }
}

/// Fest eingebauter Public Key des Signaturservers (PRODUKTIV, identisch zu Windows/Android).
public enum EmbeddedKeys {
    public static let signingPublicKeyPem = """
    -----BEGIN PUBLIC KEY-----
    MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAhMBhplCOg5jcy2ORMbR5
    cyF08qRgUyJjsxfyYGTqwW6Y61e+hICRk9tczqHmCo9eWMS70BHZrickHsBhl0Ms
    31fymtbunk85yxbuif8EwpH84zAk4Dh1ZvkcsMD/J93bBHQ8XAD9gW5YC1pTAdBp
    toUSV+wheUoTE3DQoAAji/AOpZcd6RUSpomLdw1YwszpvPyZXvpg3BZNlQFlwdYY
    2Exrf33lzqmCOnoaxcNJlv62kpLm4k2Myyyi6pw3v21CAFHuHF/vVWvdCjZjiE/W
    KlMEDS3+xhpyh/6lXfuOcv0g08N7IsGz6Vv+qYBS/p++YMzu38Lo5DFX6Q2L96YX
    MwIDAQAB
    -----END PUBLIC KEY-----
    """
}

/// RSASSA-PSS / SHA-256 / MGF1-SHA-256 / Salt 32 – passt zu server/app/signing.py.
public final class SignatureVerifier: @unchecked Sendable {
    private let key: SecKey?

    public init(publicKeyPem: String = EmbeddedKeys.signingPublicKeyPem) {
        let b64 = publicKeyPem.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("-----") }.joined()
        guard let spki = Data(base64Encoded: b64), let pkcs1 = Self.stripSpki(spki) else { key = nil; return }
        let attrs: [String: Any] = [kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
                                    kSecAttrKeyClass as String: kSecAttrKeyClassPublic]
        key = SecKeyCreateWithData(pkcs1 as CFData, attrs as CFDictionary, nil)
    }

    /// X.509 SubjectPublicKeyInfo -> PKCS#1 RSAPublicKey (Inhalt des BIT STRING).
    static func stripSpki(_ d: Data) -> Data? {
        let b = [UInt8](d)
        var i = 0
        func readLen() -> Int? {
            guard i < b.count else { return nil }
            var len = Int(b[i]); i += 1
            if len & 0x80 != 0 {
                let n = len & 0x7f; len = 0
                for _ in 0..<n { guard i < b.count else { return nil }; len = (len << 8) | Int(b[i]); i += 1 }
            }
            return len
        }
        guard b.first == 0x30 else { return nil }
        i = 1; guard readLen() != nil else { return nil }          // äußere SEQUENCE
        guard i < b.count, b[i] == 0x30 else { return nil }
        i += 1; guard let algLen = readLen() else { return nil }    // AlgorithmIdentifier
        i += algLen
        guard i < b.count, b[i] == 0x03 else { return nil }         // BIT STRING
        i += 1; guard let bitLen = readLen(), i < b.count else { return nil }
        i += 1                                                      // ungenutzte Bits
        guard i + bitLen - 1 <= b.count else { return nil }
        return Data(b[i..<(i + bitLen - 1)])
    }

    public func verify(_ data: Data, signature: Data) -> Bool {
        guard let key else { return false }
        return SecKeyVerifySignature(key, .rsaSignatureMessagePSSSHA256, data as CFData, signature as CFData, nil)
    }

    public func open(_ env: SignedEnvelope) -> Data? {
        guard let p = Data(base64Encoded: env.payloadBase64), let s = Data(base64Encoded: env.signatureBase64),
              verify(p, signature: s) else { return nil }
        return p
    }

    public func openJson(_ env: SignedEnvelope) -> [String: Any]? {
        guard let p = open(env) else { return nil }
        return (try? JSONSerialization.jsonObject(with: p)) as? [String: Any]
    }
}

public func sha256Hex(_ d: Data) -> String {
    var h = [UInt8](repeating: 0, count: Int(CC_SHA256_DIGEST_LENGTH))
    d.withUnsafeBytes { _ = CC_SHA256($0.baseAddress, CC_LONG(d.count), &h) }
    return h.map { String(format: "%02x", $0) }.joined()
}
