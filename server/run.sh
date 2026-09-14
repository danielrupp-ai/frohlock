#!/usr/bin/env bash
# Dev-Start der FrohLock-Brücke.
set -euo pipefail
cd "$(dirname "$0")"

if [ ! -d .venv ]; then
  python3 -m venv .venv
  . .venv/bin/activate
  pip install -q -r requirements.txt
else
  . .venv/bin/activate
fi

if [ -f .env ]; then set -a; . ./.env; set +a; fi

exec uvicorn app.main:app --host "${HOST:-0.0.0.0}" --port "${PORT:-8080}"
