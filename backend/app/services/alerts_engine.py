import logging
import uuid
from datetime import datetime, timedelta, timezone

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.api.v1.websocket import alert_manager
from app.config import get_settings
from app.database import AsyncSessionLocal
from app.models.activity_event import ActivityEvent
from app.models.alert import Alert
from app.models.device import Device
from app.models.policy import Policy, PolicyRuleType
from app.models.user import User
from app.services.email import send_alert_email

logger = logging.getLogger(__name__)
settings = get_settings()


async def run_alert_evaluation() -> None:
    async with AsyncSessionLocal() as db:
        policies = (await db.execute(select(Policy).where(Policy.is_active.is_(True)))).scalars().all()
        for policy in policies:
            if policy.rule_type == PolicyRuleType.idle_threshold:
                await _evaluate_idle_threshold(db, policy)
            elif policy.rule_type == PolicyRuleType.late_login:
                await _evaluate_late_login(db, policy)
        await db.commit()


async def _evaluate_idle_threshold(db: AsyncSession, policy: Policy) -> None:
    """threshold_value = minutes of continuous idle time. Looks at each
    device's most recent idle_start/idle_end event: if the latest one is an
    idle_start (i.e. the device is currently idle) and it's been that way
    longer than the threshold, alert - once per idle period, not once per
    evaluation tick, via _alert_exists_since."""
    devices = (await db.execute(select(Device))).scalars().all()
    threshold = timedelta(minutes=policy.threshold_value)
    now = datetime.now(timezone.utc)

    for device in devices:
        result = await db.execute(
            select(ActivityEvent)
            .where(ActivityEvent.device_id == device.id, ActivityEvent.event_type.in_(("idle_start", "idle_end")))
            .order_by(ActivityEvent.timestamp_utc.desc())
            .limit(1)
        )
        latest = result.scalar_one_or_none()
        if latest is None or latest.event_type != "idle_start":
            continue

        if now - latest.timestamp_utc < threshold:
            continue

        if await _alert_exists_since(db, device.id, policy.id, latest.timestamp_utc):
            continue

        await _create_alert(
            db,
            device,
            policy,
            f"{device.machine_name} has been idle for over {policy.threshold_value} minutes "
            f"(idle since {latest.timestamp_utc.strftime('%Y-%m-%d %H:%M UTC')}).",
        )


async def _evaluate_late_login(db: AsyncSession, policy: Policy) -> None:
    """threshold_value = minutes-since-midnight-UTC cutoff. Only scans a
    recent window (2x the evaluation interval) rather than all history, since
    this only needs to catch logins that just happened."""
    cutoff_minutes = policy.threshold_value
    window_start = datetime.now(timezone.utc) - timedelta(minutes=settings.alert_evaluation_interval_minutes * 2)

    result = await db.execute(
        select(ActivityEvent).where(
            ActivityEvent.event_type == "login",
            ActivityEvent.timestamp_utc >= window_start,
        )
    )
    logins = result.scalars().all()

    for login_event in logins:
        minutes_since_midnight = login_event.timestamp_utc.hour * 60 + login_event.timestamp_utc.minute
        if minutes_since_midnight <= cutoff_minutes:
            continue

        if await _alert_exists_since(
            db, login_event.device_id, policy.id, login_event.timestamp_utc - timedelta(seconds=1)
        ):
            continue

        device = await db.get(Device, login_event.device_id)
        if device is None:
            continue

        cutoff_h, cutoff_m = divmod(cutoff_minutes, 60)
        await _create_alert(
            db,
            device,
            policy,
            f"{device.machine_name} logged in at {login_event.timestamp_utc.strftime('%H:%M UTC')}, "
            f"after the {cutoff_h:02d}:{cutoff_m:02d} UTC cutoff.",
        )


async def _alert_exists_since(db: AsyncSession, device_id: uuid.UUID, policy_id: uuid.UUID, since: datetime) -> bool:
    result = await db.execute(
        select(Alert.id).where(
            Alert.device_id == device_id,
            Alert.policy_id == policy_id,
            Alert.triggered_at >= since,
        )
    )
    return result.first() is not None


async def _create_alert(db: AsyncSession, device: Device, policy: Policy, message: str) -> None:
    alert = Alert(device_id=device.id, policy_id=policy.id, message=message)
    db.add(alert)
    await db.flush()

    await alert_manager.broadcast(
        {
            "id": str(alert.id),
            "device_id": str(device.id),
            "device_name": device.machine_name,
            "policy_id": str(policy.id),
            "message": message,
            "triggered_at": alert.triggered_at.isoformat() if alert.triggered_at else None,
        }
    )

    if device.assigned_user_id is not None:
        user = await db.get(User, device.assigned_user_id)
        supervisor = await db.get(User, user.supervisor_id) if user and user.supervisor_id else None
        if supervisor is not None:
            await send_alert_email(supervisor.email, device.machine_name, message)

    logger.info("Alert created: device=%s policy=%s", device.machine_name, policy.name)
