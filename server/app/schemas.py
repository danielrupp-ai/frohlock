"""Pydantic-Modelle für die Geräte-API."""
from __future__ import annotations

from pydantic import BaseModel, Field


class RegisterRequest(BaseModel):
    pairingCode: str
    deviceName: str = "Gerät"


class RegisterResponse(BaseModel):
    deviceId: str
    token: str


class ScheduleWindowIn(BaseModel):
    id: str | None = None
    label: str | None = None
    days: list[int] = Field(default_factory=list)   # 0=So .. 6=Sa
    startMinute: int = 0
    endMinute: int = 0
    enabled: bool = True


class ConfigDraft(BaseModel):
    """Vom Setup (Eltern) oder Admin gesetzte Werte. PIN kommt bereits als Hash."""
    windows: list[ScheduleWindowIn] = Field(default_factory=list)
    pinHash: str = ""
    pinSalt: str = ""
    pinIterations: int = 210_000
    unlockGraceMinutes: int = 60
    dailyBudgetMinutes: int = 0
    maxTrustedTimeStalenessMinutes: int = 720
    resetEmail: str = ""


class HeartbeatIn(BaseModel):
    deviceId: str = ""
    appVersion: str = ""
    configVersion: int = 0
    currentlyLocked: bool = False
    trustedTimeUnix: int = 0
    trustedTimeAgeSeconds: int = 0
    lockReason: str = ""
    pinFailuresToday: int = 0
    lastBootUnix: int = 0
