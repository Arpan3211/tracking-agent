from datetime import datetime

from fastapi import APIRouter, Depends, Query
from fastapi.responses import StreamingResponse
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.api.deps import get_current_user
from app.api.v1.devices import get_accessible_device
from app.database import get_db
from app.models.activity_event import ActivityEvent
from app.models.device import Device
from app.models.user import User
from app.services.audit import record_audit
from app.services.export import export_events_csv, export_events_xlsx

router = APIRouter(prefix="/reports", tags=["reports"])


@router.get("/export")
async def export_report(
    format: str = Query(..., pattern="^(csv|xlsx)$"),
    from_: datetime | None = Query(default=None, alias="from"),
    to: datetime | None = Query(default=None),
    # get_accessible_device takes device_id as a plain parameter; since this
    # route's own path has no {device_id} placeholder, FastAPI resolves it
    # as a query parameter here instead - same authorization check as the
    # /devices/{device_id}/* routes, reused rather than duplicated.
    device: Device = Depends(get_accessible_device),
    current_user: User = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
) -> StreamingResponse:
    query = select(ActivityEvent).where(ActivityEvent.device_id == device.id)
    if from_ is not None:
        query = query.where(ActivityEvent.timestamp_utc >= from_)
    if to is not None:
        query = query.where(ActivityEvent.timestamp_utc <= to)
    query = query.order_by(ActivityEvent.timestamp_utc)

    events = list((await db.execute(query)).scalars().all())

    # Exporting raw activity data is exactly the kind of action the
    # compliance audit trail exists for.
    await record_audit(db, current_user.id, "export_report", target=f"device={device.machine_name}; format={format}")
    await db.commit()

    filename = f"{device.machine_name}_activity.{format}"
    if format == "csv":
        buffer = export_events_csv(events)
        media_type = "text/csv"
    else:
        buffer = export_events_xlsx(events)
        media_type = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"

    return StreamingResponse(
        buffer,
        media_type=media_type,
        headers={"Content-Disposition": f'attachment; filename="{filename}"'},
    )
