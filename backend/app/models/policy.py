import enum
import uuid
from datetime import datetime

from sqlalchemy import Boolean, DateTime, Enum, ForeignKey, Integer, String, func
from sqlalchemy.dialects.postgresql import UUID
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.database import Base


class PolicyRuleType(str, enum.Enum):
    idle_threshold = "idle_threshold"
    late_login = "late_login"


class Policy(Base):
    """threshold_value's meaning depends on rule_type: for idle_threshold,
    minutes of continuous idle time that trigger an alert; for late_login,
    minutes-since-midnight-UTC cutoff (e.g. 570 = 09:30 UTC) after which a
    login counts as late."""

    __tablename__ = "policies"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    name: Mapped[str] = mapped_column(String(255), nullable=False)
    rule_type: Mapped[PolicyRuleType] = mapped_column(
        Enum(PolicyRuleType, name="policy_rule_type", values_callable=lambda e: [m.value for m in e]),
        nullable=False,
    )
    threshold_value: Mapped[int] = mapped_column(Integer, nullable=False)
    created_by: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id"), nullable=False)
    is_active: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())

    creator: Mapped["User"] = relationship("User")  # noqa: F821
