import uuid

from sqlalchemy.ext.asyncio import AsyncSession

from app.models.audit_log import AuditLog


async def record_audit(db: AsyncSession, actor_user_id: uuid.UUID, action: str, target: str | None = None) -> None:
    """Adds an AuditLog row to the current session without committing - the
    caller's own db.commit() (right after their real write) covers this too,
    so an audit entry and the action it records always land atomically."""
    db.add(AuditLog(actor_user_id=actor_user_id, action=action, target=target))
