import uuid
from datetime import datetime

from pydantic import BaseModel, ConfigDict


class AlertOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: uuid.UUID
    device_id: uuid.UUID
    policy_id: uuid.UUID
    triggered_at: datetime
    message: str
    is_read: bool
    acknowledged_by: uuid.UUID | None
    acknowledged_at: datetime | None
