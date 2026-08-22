import uuid

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models.user import User, UserRole


async def get_visible_user_ids(db: AsyncSession, current_user: User) -> list[uuid.UUID] | None:
    """Returns the user IDs whose devices `current_user` is allowed to see,
    or None to mean 'no restriction' (HR/Admin see everyone). Employees see
    only themselves; supervisors see themselves plus their direct reports
    (one level - the spec doesn't ask for a full org-chart rollup). Callers
    filter device queries with `Device.assigned_user_id.in_(ids)` when this
    returns a list, and skip the filter entirely when it returns None."""
    if current_user.role in (UserRole.hr, UserRole.admin):
        return None

    if current_user.role == UserRole.supervisor:
        result = await db.execute(select(User.id).where(User.supervisor_id == current_user.id))
        report_ids = [row[0] for row in result.all()]
        return [current_user.id, *report_ids]

    return [current_user.id]
