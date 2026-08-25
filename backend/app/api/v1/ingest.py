from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.api.deps import get_current_device
from app.core.security import generate_api_key, hash_api_key
from app.database import get_db
from app.models.activity_event import ActivityEvent
from app.models.device import Device
from app.schemas.activity import IngestRequest, IngestResponse
from app.schemas.device import DeviceEnrollRequest, DeviceEnrollResponse

router = APIRouter()


@router.post("/devices/enroll", response_model=DeviceEnrollResponse, status_code=status.HTTP_201_CREATED)
async def enroll_device(payload: DeviceEnrollRequest, db: AsyncSession = Depends(get_db)) -> DeviceEnrollResponse:
    """Called once during agent setup, not on every run. Re-enrolling an
    already-known machine_name (agent reinstalled, etc.) issues a fresh key
    rather than erroring, silently invalidating the old one."""
    result = await db.execute(select(Device).where(Device.machine_name == payload.machine_name))
    device = result.scalar_one_or_none()

    api_key = generate_api_key()
    key_hash = hash_api_key(api_key)

    if device is not None:
        device.api_key_hash = key_hash
        device.os_version = payload.os_version
    else:
        device = Device(machine_name=payload.machine_name, os_version=payload.os_version, api_key_hash=key_hash)
        db.add(device)

    await db.commit()
    await db.refresh(device)

    return DeviceEnrollResponse(device_id=device.id, machine_name=device.machine_name, api_key=api_key)


@router.post("/ingest/events", response_model=IngestResponse)
async def ingest_events(
    payload: IngestRequest,
    device: Device = Depends(get_current_device),
    db: AsyncSession = Depends(get_db),
) -> IngestResponse:
    if payload.machine_name != device.machine_name:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="machine_name does not match the device this API key was issued to",
        )

    for event in payload.events:
        db.add(
            ActivityEvent(
                device_id=device.id,
                event_type=event.event_type,
                timestamp_utc=event.timestamp_utc,
                details=event.details,
            )
        )

    device.last_seen_at = datetime.now(timezone.utc)

    await db.commit()

    return IngestResponse(accepted=len(payload.events), device_id=device.id)
