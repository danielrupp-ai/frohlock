# Server-Deploy (Hetzner, neben den übrigen Standalone-Diensten)

Die Brücke läuft als eigener Container am externen Docker-Netz `caddy_proxy`; die
bestehende Caddy-Instanz (`fzr_caddy`) routet `frohlock.froehlichdienste.de` per
conf.d-Import auf ihn.

## Layout auf dem Server
```
~/apps/frohlock/
  repo/                     git clone dieses Repos (Quelle)
  secrets/signing_private.pem   auf dem Server erzeugt, verlässt ihn nie
  data/                     SQLite-DB (app.db)
  docker-compose.yml        aus deploy/docker-compose.example.yml
  frohlock.env              aus deploy/frohlock.env.example (chmod 600)
```

## Erstinstallation
```bash
mkdir -p ~/apps/frohlock/secrets ~/apps/frohlock/data
# Prod-Schlüsselpaar (privat bleibt hier, public in den Client eingebaut):
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out ~/apps/frohlock/secrets/signing_private.pem
openssl rsa -in ~/apps/frohlock/secrets/signing_private.pem -pubout -out ~/apps/frohlock/secrets/signing_public.pem
chmod 600 ~/apps/frohlock/secrets/signing_private.pem

git clone https://github.com/danielrupp-ai/frohlock ~/apps/frohlock/repo
cp ~/apps/frohlock/repo/deploy/docker-compose.example.yml ~/apps/frohlock/docker-compose.yml
cp ~/apps/frohlock/repo/deploy/frohlock.env.example       ~/apps/frohlock/frohlock.env
# frohlock.env ausfüllen (Admin-PW, Session-Secret) …

cd ~/apps/frohlock && docker compose up -d --build

# Caddy-Route:
cp ~/apps/frohlock/repo/deploy/frohlock.caddy /home/deploy/infra/caddy/conf.d/frohlock.caddy
docker exec fzr_caddy caddy reload --config /etc/caddy/Caddyfile
```

## DNS (einmalig, durch den Domain-Inhaber)
GoDaddy A-Record `frohlock` → `46.224.7.46`. Erst danach stellt Caddy automatisch
das Let's-Encrypt-Zertifikat aus und die Domain ist live.

## Update
```bash
cd ~/apps/frohlock/repo && git pull
cd ~/apps/frohlock && docker compose up -d --build
```
