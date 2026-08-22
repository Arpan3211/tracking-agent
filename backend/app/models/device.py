import uuid
from datetime import datetime

from sqlalchemy import DateTime, ForeignKey, String, func
from sqlalchemy.dialects.postgresql import UUID
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.database import Base


class Device(Base):
    __tablename__ = "devices"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)
    machine_name: Mapped[str] = mapped_column(String(255), unique=True, index=True, nullable=False)
    assigned_user_id: Mapped[uuid.UUID | None] = mapped_column(
        UUID(as_uuid=True), ForeignKey("users.id"), nullable=True
    )
    os_version: Mapped[str | None] = mapped_column(String(255), nullable=True)
    # Only a hash of the enrollment API key is stored (same principle as a
    # password) - the plaintext key is returned once at /devices/enroll and
    # never persisted, so a DB leak alone can't be used to impersonate a device.
    api_key_hash: Mapped[str] = mapped_column(String(255), nullable=False)
    first_seen_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    last_seen_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)

    assigned_user: Mapped["User | None"] = relationship("User", back_populates="devices")  # noqa: F821
