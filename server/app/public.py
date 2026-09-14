"""Öffentliche Seiten: Download-Landingpage + Auslieferung der Setup-.exe."""
from __future__ import annotations

import os
from pathlib import Path

from fastapi import APIRouter, Request
from fastapi.responses import FileResponse, HTMLResponse
from fastapi.templating import Jinja2Templates

from .config import settings

router = APIRouter()
templates = Jinja2Templates(directory=str(Path(__file__).parent / "templates"))

INSTALLER_NAME = "FrohLockSetup.exe"


def _installer_path() -> str:
    return os.path.join(settings.download_dir, INSTALLER_NAME)


def _version() -> str:
    try:
        with open(os.path.join(settings.download_dir, "version.txt")) as fh:
            return fh.read().strip()
    except Exception:
        return ""


def _size_mb() -> int:
    try:
        return round(os.path.getsize(_installer_path()) / (1024 * 1024))
    except Exception:
        return 0


@router.get("/", response_class=HTMLResponse)
def landing(request: Request):
    return templates.TemplateResponse("landing.html", {
        "request": request,
        "version": _version(),
        "size_mb": _size_mb(),
        "available": os.path.exists(_installer_path()),
    })


@router.get("/download")
def download():
    path = _installer_path()
    if not os.path.exists(path):
        return HTMLResponse("<h1>Download derzeit nicht verfügbar</h1>", status_code=404)
    return FileResponse(path, media_type="application/octet-stream", filename=INSTALLER_NAME)
