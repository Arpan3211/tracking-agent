import uuid
from datetime import datetime

from sqlalchemy import DateTime, ForeignKey, Index, String, Text, func
from sqlalchemy.dialects.postgresql import JSONB, UUID
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.database import Base


class ActivityEvent(Base):
    __tablename__ = "activity_events"
    __table_args__ = (
        # Highest-volume table by far - every dashboard drill-down and the
        # aggregation job filter by device + time range, so this composite
        # index is the one that matters most.
        Index("ix_activity_events_device_timestamp", "device_id", "timestamp_utc"),
        Index("ix_activity_events_event_type", "event_type"),
    )

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    device_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("devices.id"), nullable=False)
    event_type: Mapped[str] = mapped_column(String(100), nullable=False)
    timestamp_utc: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    # Raw "key=value; key2=value2" string exactly as the agent sent it, kept
    # verbatim for auditability/debugging even though details_parsed is what
    # queries actually use.
    details_raw: Mapped[str | None] = mapped_column(Text, nullable=True)
    details_parsed: Mapped[dict | None] = mapped_column(JSONB, nullable=True)
    received_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())

    device: Mapped["Device"] = relationship("Device")  # noqa: F821
