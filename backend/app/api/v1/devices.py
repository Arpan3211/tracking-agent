import uuid
from datetime import date, datetime

from fastapi import APIRouter, Depends, HTTPException, Query, status
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.api.deps import get_current_user
from app.core.rbac import get_visible_user_ids
from app.database import get_db
from app.models.activity_event import ActivityEvent
from app.models.daily_activity_summary import DailyActivitySummary
from app.models.device import Device
from app.models.session import DeviceSession
from app.models.user import User
from app.schemas.activity import ActivityEventOut, ActivitySummaryOut, IdlePeriodOut
from app.schemas.device import DeviceOut
from app.schemas.pagination import Page
from app.schemas.session import SessionOut

router = APIRouter(prefix="/devices", tags=["devices"])


async def get_accessible_device(
    device_id: uuid.UUID,
    current_user: User = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
) -> Device:
    """Shared row-level authorization for every /devices/{device_id}/* route:
    404 if the device doesn't exist, 403 if it exists but the caller's role
    scope (see app/core/rbac.py) doesn't include its assigned user. 404
    before 403 so an unauthorized caller can't distinguish "doesn't exist"
    from "exists but not yours" by response code."""
    device = await db.get(Device, device_id)
    if device is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Device not found")

    visible_ids = await get_visible_user_ids(db, current_user)
    if visible_ids is not None and device.assigned_user_id not in visible_ids:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Not authorized for this device")

    return device


@router.get("", response_model=list[DeviceOut])
async def list_devices(
    current_user: User = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
) -> list[Device]:
    visible_ids = await get_visible_user_ids(db, current_user)
    query = select(Device)
    if visible_ids is not None:
        query = query.where(Device.assigned_user_id.in_(visible_ids))
    result = await db.execute(query.order_by(Device.machine_name))
    return list(result.scalars().all())


@router.get("/{device_id}/sessions", response_model=list[SessionOut])
async def get_device_sessions(
    device: Device = Depends(get_accessible_device),
    db: AsyncSession = Depends(get_db),
    limit: int = Query(default=50, le=200),
) -> list[DeviceSession]:
    result = await db.execute(
        select(DeviceSession)
        .where(DeviceSession.device_id == device.id)
        .order_by(DeviceSession.login_at.desc())
        .limit(limit)
    )
    return list(result.scalars().all())


@router.get("/{device_id}/activity-summary", response_model=list[ActivitySummaryOut])
async def get_activity_summary(
    device: Device = Depends(get_accessible_device),
    db: AsyncSession = Depends(get_db),
    from_: date | None = Query(default=None, alias="from"),
    to: date | None = Query(default=None),
) -> list[DailyActivitySummary]:
    query = select(DailyActivitySummary).where(DailyActivitySummary.device_id == device.id)
    if from_ is not None:
        query = query.where(DailyActivitySummary.date >= from_)
    if to is not None:
        query = query.where(DailyActivitySummary.date <= to)
    result = await db.execute(query.order_by(DailyActivitySummary.date))
    return list(result.scalars().all())


@router.get("/{device_id}/idle-periods", response_model=list[IdlePeriodOut])
async def get_idle_periods(
    device: Device = Depends(get_accessible_device),
    db: AsyncSession = Depends(get_db),
    from_: datetime | None = Query(default=None, alias="from"),
    to: datetime | None = Query(default=None),
    limit: int = Query(default=100, le=500),
) -> list[IdlePeriodOut]:
    """Pairs idle_start/idle_end events into (start, end, duration) periods -
    the per-pause breakdown behind DailyActivitySummary.total_idle_seconds,
    which only has the daily total, not individual pauses. Computed here on
    the fly rather than stored, same pairing approach as aggregation.py's
    _compute_idle_periods() but exposed for direct display instead of being
    an intermediate step toward a daily sum."""
    query = select(ActivityEvent).where(
        ActivityEvent.device_id == device.id,
        ActivityEvent.event_type.in_(("idle_start", "idle_end")),
    )
    if from_ is not None:
        query = query.where(ActivityEvent.timestamp_utc >= from_)
    if to is not None:
        query = query.where(ActivityEvent.timestamp_utc <= to)
    result = await db.execute(query.order_by(ActivityEvent.timestamp_utc))
    events = result.scalars().all()

    periods: list[IdlePeriodOut] = []
    idle_start: datetime | None = None
    for event in events:
        if event.event_type == "idle_start":
            idle_start = event.timestamp_utc
        elif event.event_type == "idle_end" and idle_start is not None:
            duration = int((event.timestamp_utc - idle_start).total_seconds())
            periods.append(IdlePeriodOut(start=idle_start, end=event.timestamp_utc, duration_seconds=duration))
            idle_start = None

    # A trailing idle_start with no matching idle_end yet means the device
    # is idle right now - surface it as an ongoing period, same convention
    # get_device_sessions uses for a session with no logout_at yet.
    if idle_start is not None:
        periods.append(IdlePeriodOut(start=idle_start, end=None, duration_seconds=None))

    periods.reverse()  # most recent first, matching get_device_sessions
    return periods[:limit]


@router.get("/{device_id}/events", response_model=Page[ActivityEventOut])
async def get_device_events(
    device: Device = Depends(get_accessible_device),
    db: AsyncSession = Depends(get_db),
    event_type: list[str] | None = Query(default=None),
    from_: datetime | None = Query(default=None, alias="from"),
    to: datetime | None = Query(default=None),
    page: int = Query(default=1, ge=1),
    page_size: int = Query(default=50, le=500),
) -> Page[ActivityEventOut]:
    query = select(ActivityEvent).where(ActivityEvent.device_id == device.id)
    if event_type:
        query = query.where(ActivityEvent.event_type.in_(event_type))
    if from_ is not None:
        query = query.where(ActivityEvent.timestamp_utc >= from_)
    if to is not None:
        query = query.where(ActivityEvent.timestamp_utc <= to)

    total = (await db.execute(select(func.count()).select_from(query.subquery()))).scalar_one()

    query = query.order_by(ActivityEvent.timestamp_utc.desc()).offset((page - 1) * page_size).limit(page_size)
    items = list((await db.execute(query)).scalars().all())

    return Page(items=items, total=total, page=page, page_size=page_size)
