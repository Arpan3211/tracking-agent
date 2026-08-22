import uuid
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, Query, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.api.deps import get_current_user
from app.core.csrf import verify_csrf
from app.core.rbac import get_visible_user_ids
from app.database import get_db
from app.models.alert import Alert
from app.models.device import Device
from app.models.user import User
from app.schemas.alert import AlertOut

router = APIRouter(prefix="/alerts", tags=["alerts"])


@router.get("", response_model=list[AlertOut])
async def list_alerts(
    current_user: User = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
    is_read: bool | None = Query(default=None),
    device_id: uuid.UUID | None = Query(default=None),
    limit: int = Query(default=100, le=500),
) -> list[Alert]:
    visible_ids = await get_visible_user_ids(db, current_user)

    query = select(Alert).join(Device, Alert.device_id == Device.id)
    if visible_ids is not None:
        query = query.where(Device.assigned_user_id.in_(visible_ids))
    if is_read is not None:
        query = query.where(Alert.is_read == is_read)
    if device_id is not None:
        query = query.where(Alert.device_id == device_id)

    query = query.order_by(Alert.triggered_at.desc()).limit(limit)
    result = await db.execute(query)
    return list(result.scalars().all())


@router.post("/{alert_id}/acknowledge", response_model=AlertOut, dependencies=[Depends(verify_csrf)])
async def acknowledge_alert(
    alert_id: uuid.UUID,
    current_user: User = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
) -> Alert:
    alert = await db.get(Alert, alert_id)
    if alert is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Alert not found")

    device = await db.get(Device, alert.device_id)
    visible_ids = await get_visible_user_ids(db, current_user)
    if visible_ids is not None and (device is None or device.assigned_user_id not in visible_ids):
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Not authorized for this alert")

    alert.is_read = True
    alert.acknowledged_by = current_user.id
    alert.acknowledged_at = datetime.now(timezone.utc)
    await db.commit()
    await db.refresh(alert)
    return alert
