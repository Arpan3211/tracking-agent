import uuid
from datetime import date, datetime

from pydantic import BaseModel, ConfigDict, Field


class ActivityEventIn(BaseModel):
    event_type: str
    timestamp_utc: datetime
    details: str | None = None


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
    details_raw: str | None
    details_parsed: dict | None
    received_at: datetime


class ActivitySummaryOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    device_id: uuid.UUID
    date: date
    total_active_seconds: int
    total_idle_seconds: int
    top_apps: dict | None
    event_count: int
