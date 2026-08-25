import uuid
from datetime import date, datetime

from pydantic import BaseModel, ConfigDict, Field


class ActivityEventIn(BaseModel):
    event_type: str
    timestamp_utc: datetime
    # Structured per-event-type payload the agent builds directly (e.g.
    # {"process": "chrome", "title": "..."}), not a delimited string - stored
    # as-is in activity_events.details (JSONB), no server-side parsing.
    details: dict[str, str] | None = None


class IngestRequest(BaseModel):
    machine_name: str
    # Cap batch size so one malformed/malicious payload can't force a huge
    # single INSERT transaction - the agent is expected to batch every ~30s
    # or ~50 events per README's sync-loop design, well under this.
    events: list[ActivityEventIn] = Field(min_length=1, max_length=500)


class IngestResponse(BaseModel):
    accepted: int
    device_id: uuid.UUID


class ActivityEventOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: uuid.UUID
    device_id: uuid.UUID
    event_type: str
    timestamp_utc: datetime
    details: dict | None
    received_at: datetime


class ActivitySummaryOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    device_id: uuid.UUID
    date: date
    total_active_seconds: int
    total_idle_seconds: int
    top_apps: dict | None
    event_count: int


class IdlePeriodOut(BaseModel):
    """One idle_start->idle_end pair, computed on the fly from raw events
    (not stored - see get_idle_periods in app/api/v1/devices.py). end/
    duration_seconds are null for a period that's still ongoing (the device
    is idle right now and hasn't sent a matching idle_end yet)."""

    start: datetime
    end: datetime | None
    duration_seconds: int | None
