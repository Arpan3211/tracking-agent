import uuid
from datetime import datetime

from pydantic import BaseModel, ConfigDict

from app.models.policy import PolicyRuleType


class PolicyCreate(BaseModel):
    name: str
    rule_type: PolicyRuleType
    threshold_value: int
    is_active: bool = True


class PolicyUpdate(BaseModel):
    name: str | None = None
    threshold_value: int | None = None
    is_active: bool | None = None


class PolicyOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: uuid.UUID
    name: str
    rule_type: PolicyRuleType
    threshold_value: int
    created_by: uuid.UUID
    is_active: bool
    created_at: datetime
