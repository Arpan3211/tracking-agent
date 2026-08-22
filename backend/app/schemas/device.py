import uuid
from datetime import datetime

from pydantic import BaseModel, ConfigDict


class DeviceEnrollRequest(BaseModel):
    machine_name: str
    os_version: str | None = None


class DeviceEnrollResponse(BaseModel):
    device_id: uuid.UUID
    machine_name: str
    # Plaintext API key - returned ONCE at enrollment and never again. The
    # agent must cache it locally; the server only ever stores its hash.
    api_key: str


class DeviceOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: uuid.UUID
    machine_name: str
    os_version: str | None
    assigned_user_id: uuid.UUID | None
    first_seen_at: datetime
    last_seen_at: datetime | None
