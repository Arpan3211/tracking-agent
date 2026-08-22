import uuid
from datetime import date as date_type

from sqlalchemy import ForeignKey, Integer, UniqueConstraint
from sqlalchemy import Date as SqlDate
from sqlalchemy.dialects.postgresql import JSONB, UUID
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.database import Base


class DailyActivitySummary(Base):
    """Pre-aggregated per-device-per-day rollup, populated by the scheduled
    aggregation job so dashboard queries don't have to scan raw
    activity_events every time."""

    __tablename__ = "daily_activity_summary"
    __table_args__ = (UniqueConstraint("device_id", "date", name="uq_daily_summary_device_date"),)

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    device_id: Mapped[uuid.UUID] = mapped_column(
        UUID(as_uuid=True), ForeignKey("devices.id"), nullable=False, index=True
    )
    date: Mapped[date_type] = mapped_column(SqlDate, nullable=False)
    total_active_seconds: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    total_idle_seconds: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    top_apps: Mapped[dict | None] = mapped_column(JSONB, nullable=True)
    event_count: Mapped[int] = mapped_column(Integer, nullable=False, default=0)

    device: Mapped["Device"] = relationship("Device")  # noqa: F821
