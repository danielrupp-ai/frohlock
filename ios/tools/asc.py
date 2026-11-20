"""Kleiner App-Store-Connect-API-Helfer für FrohLock iOS (Geräte, Bundle-IDs, Profile). Schlüssel lokal, nie im Repo."""
import base64, json, os, sys, time, urllib.request, urllib.error
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature

KEY_ID = os.environ.get("ASC_KEY_ID", "BU4HQY7VTY")
ISSUER = os.environ.get("ASC_ISSUER", "1a5b6712-e2ac-40bd-aee6-1b82e955988d")
P8 = os.path.expanduser(os.environ.get("ASC_P8", f"~/.appstoreconnect/private_keys/AuthKey_{KEY_ID}.p8"))


def _b64(b): return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def token():
    key = serialization.load_pem_private_key(open(P8, "rb").read(), None)
    h = {"alg": "ES256", "kid": KEY_ID, "typ": "JWT"}
    p = {"iss": ISSUER, "iat": int(time.time()), "exp": int(time.time()) + 1100, "aud": "appstoreconnect-v1"}
    msg = f"{_b64(json.dumps(h).encode())}.{_b64(json.dumps(p).encode())}".encode()
    r, s = decode_dss_signature(key.sign(msg, ec.ECDSA(hashes.SHA256())))
    return msg.decode() + "." + _b64(r.to_bytes(32, "big") + s.to_bytes(32, "big"))


def call(method, path, body=None):
    import ssl
    try:
        import certifi; ctx = ssl.create_default_context(cafile=certifi.where())
    except Exception:
        ctx = ssl.create_default_context()
    req = urllib.request.Request("https://api.appstoreconnect.apple.com" + path, method=method,
                                 data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Authorization": "Bearer " + token(), "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, context=ctx) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{method} {path} -> {e.code}: {e.read().decode()[:800]}")


if __name__ == "__main__":
    m, path = sys.argv[1], sys.argv[2]
    body = json.loads(sys.argv[3]) if len(sys.argv) > 3 else None
    print(json.dumps(call(m, path, body), indent=1, ensure_ascii=False))
