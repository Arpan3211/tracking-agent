import uuid
from datetime import datetime

from pydantic import BaseModel, ConfigDict


class SessionOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: uuid.UUID
    device_id: uuid.UUID
    login_at: datetime
    logout_at: datetime | None
    duration_seconds: int | None
