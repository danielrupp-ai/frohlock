"""Schlanke SQLite-Schicht (stdlib) mit Schema-Init. Bewusst dependency-arm."""
from __future__ import annotations

import sqlite3
import threading
from contextlib import contextmanager

from .config import settings

_lock = threading.Lock()

SCHEMA = """
CREATE TABLE IF NOT EXISTS devices (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    token_hash TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    last_seen INTEGER,
    app_version TEXT,
    reported_config_version INTEGER DEFAULT 0,
    locked INTEGER DEFAULT 0,
    lock_reason TEXT,
    trusted_time_age INTEGER,
    pin_failures INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS device_config (
    device_id TEXT PRIMARY KEY,
    config_version INTEGER NOT NULL,
    config_json TEXT NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS pairing_codes (
    code TEXT PRIMARY KEY,
    device_name TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    used_device_id TEXT
);

CREATE TABLE IF NOT EXISTS commands (
    id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL,
    type INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    issued_at INTEGER NOT NULL,
    expires_at INTEGER DEFAULT 0,
    acked INTEGER DEFAULT 0,
    created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS audit (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    actor TEXT NOT NULL,
    device_id TEXT,
    action TEXT NOT NULL,
    detail TEXT
);

CREATE TABLE IF NOT EXISTS reset_tokens (
    token TEXT PRIMARY KEY,
    device_id TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    used INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS usage_daily (
    device_id TEXT NOT NULL,
    day TEXT NOT NULL,
    minutes INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (device_id, day)
);
"""

# Leichte Migrationen für bestehende DBs.
MIGRATIONS = [
    "ALTER TABLE devices ADD COLUMN reset_email TEXT",
    "ALTER TABLE devices ADD COLUMN usage_today INTEGER DEFAULT 0",
    "ALTER TABLE devices ADD COLUMN budget_minutes INTEGER DEFAULT 0",
]


def _connect() -> sqlite3.Connection:
    conn = sqlite3.connect(settings.db_path, check_same_thread=False)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL;")
    conn.execute("PRAGMA foreign_keys=ON;")
    return conn


_conn = _connect()


def init_db() -> None:
    with _lock:
        _conn.executescript(SCHEMA)
        for stmt in MIGRATIONS:
            try:
                _conn.execute(stmt)
            except sqlite3.OperationalError:
                pass  # Spalte existiert bereits
        _conn.commit()


@contextmanager
def tx():
    """Kurze Transaktion, serialisiert (SQLite mag keine parallelen Writer)."""
    with _lock:
        try:
            yield _conn
            _conn.commit()
        except Exception:
            _conn.rollback()
            raise


def query(sql: str, params: tuple = ()) -> list[sqlite3.Row]:
    with _lock:
        return list(_conn.execute(sql, params).fetchall())


def query_one(sql: str, params: tuple = ()):
    with _lock:
        return _conn.execute(sql, params).fetchone()


def audit(actor: str, action: str, device_id: str | None = None, detail: str = "") -> None:
    import time
    with tx() as c:
        c.execute(
            "INSERT INTO audit(ts, actor, device_id, action, detail) VALUES (?,?,?,?,?)",
            (int(time.time()), actor, device_id, action, detail),
        )
