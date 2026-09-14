"""FrohLock-Brücke: FastAPI-App (Geräte-API + Admin-Oberfläche)."""
from __future__ import annotations

from fastapi import FastAPI
from starlette.middleware.sessions import SessionMiddleware

from . import admin, api, db, reset
from .config import settings
from .security import hash_password


def create_app() -> FastAPI:
    app = FastAPI(title="FrohLock Brücke", docs_url=None, redoc_url=None)
    app.add_middleware(SessionMiddleware, secret_key=settings.session_secret,
                       https_only=False, same_site="lax")

    db.init_db()
    # Admin-Passwort-Hash im Speicher halten (kein Klartext persistiert).
    app.state.admin_password_hash = hash_password(settings.admin_password)

    app.include_router(api.router)
    app.include_router(reset.router)
    app.include_router(admin.router)

    @app.get("/healthz")
    def healthz():
        return {"ok": True}

    return app


app = create_app()
