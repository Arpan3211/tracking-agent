import logging
import uuid
from collections import defaultdict
from datetime import date as date_type
from datetime import datetime

from sqlalchemy import delete, select
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.ext.asyncio import AsyncSession

from app.database import AsyncSessionLocal
from app.models.activity_event import ActivityEvent
from app.models.daily_activity_summary import DailyActivitySummary
from app.models.device import Device
from app.models.session import DeviceSession

logger = logging.getLogger(__name__)


async def run_aggregation() -> None:
    """Scheduler entry point. Recomputes sessions and daily_activity_summary
    for every device from full event history each run: pilot-scale
    simplicity over incremental-update complexity. Delete-then-rebuild is
    idempotent and cheap enough at this data volume - revisit with a proper
    incremental/watermark approach if activity_events ever grows large
    enough to make a full per-device scan too slow."""
    async with AsyncSessionLocal() as db:
        device_ids = (await db.execute(select(Device.id))).scalars().all()
        for device_id in device_ids:
            await _rebuild_sessions(db, device_id)
            await _rebuild_daily_summaries(db, device_id)
        await db.commit()
    logger.info("Aggregation pass complete for %d device(s)", len(device_ids))


async def _rebuild_sessions(db: AsyncSession, device_id: uuid.UUID) -> None:
    result = await db.execute(
        select(ActivityEvent)
        .where(ActivityEvent.device_id == device_id, ActivityEvent.event_type.in_(("login", "logout")))
        .order_by(ActivityEvent.timestamp_utc)
    )
    events = result.scalars().all()

    await db.execute(delete(DeviceSession).where(DeviceSession.device_id == device_id))

    login_at: datetime | None = None
    for event in events:
        if event.event_type == "login":
            login_at = event.timestamp_utc
        elif event.event_type == "logout" and login_at is not None:
            duration = int((event.timestamp_utc - login_at).total_seconds())
            db.add(
                DeviceSession(
                    device_id=device_id,
                    login_at=login_at,
                    logout_at=event.timestamp_utc,
                    duration_seconds=max(0, duration),
                )
            )
            login_at = None

    # A trailing login with no matching logout yet is a currently-open session.
    if login_at is not None:
        db.add(DeviceSession(device_id=device_id, login_at=login_at, logout_at=None, duration_seconds=None))


async def _rebuild_daily_summaries(db: AsyncSession, device_id: uuid.UUID) -> None:
    result = await db.execute(
        select(ActivityEvent).where(ActivityEvent.device_id == device_id).order_by(ActivityEvent.timestamp_utc)
    )
    events = result.scalars().all()
    if not events:
        return

    events_by_date: dict[date_type, list[ActivityEvent]] = defaultdict(list)
    for event in events:
        events_by_date[event.timestamp_utc.date()].append(event)

    for day, day_events in events_by_date.items():
        idle_periods = _compute_idle_periods(day_events)
        active_seconds, idle_seconds = _compute_active_idle_seconds(day_events, idle_periods)
        top_apps = _compute_top_apps(day_events, idle_periods)

        stmt = pg_insert(DailyActivitySummary).values(
            device_id=device_id,
            date=day,
            total_active_seconds=active_seconds,
            total_idle_seconds=idle_seconds,
            top_apps=top_apps,
            event_count=len(day_events),
        )
        stmt = stmt.on_conflict_do_update(
            index_elements=[DailyActivitySummary.device_id, DailyActivitySummary.date],
            set_={
                "total_active_seconds": stmt.excluded.total_active_seconds,
                "total_idle_seconds": stmt.excluded.total_idle_seconds,
                "top_apps": stmt.excluded.top_apps,
                "event_count": stmt.excluded.event_count,
            },
        )
        await db.execute(stmt)


def _compute_idle_periods(events: list[ActivityEvent]) -> list[tuple[datetime, datetime]]:
    periods: list[tuple[datetime, datetime]] = []
    idle_start: datetime | None = None
    for e in events:
        if e.event_type == "idle_start":
            idle_start = e.timestamp_utc
        elif e.event_type == "idle_end" and idle_start is not None:
            periods.append((idle_start, e.timestamp_utc))
            idle_start = None
    return periods


def _overlap_seconds(a_start: datetime, a_end: datetime, b_start: datetime, b_end: datetime) -> float:
    latest_start = max(a_start, b_start)
    earliest_end = min(a_end, b_end)
    return max(0.0, (earliest_end - latest_start).total_seconds())


def _compute_active_idle_seconds(
    events: list[ActivityEvent], idle_periods: list[tuple[datetime, datetime]]
) -> tuple[int, int]:
    if not events:
        return 0, 0

    span_start, span_end = events[0].timestamp_utc, events[-1].timestamp_utc
    total_span = max(0.0, (span_end - span_start).total_seconds())

    idle_seconds = sum(_overlap_seconds(span_start, span_end, start, end) for start, end in idle_periods)
    idle_seconds = min(idle_seconds, total_span)
    active_seconds = max(0.0, total_span - idle_seconds)
    return int(active_seconds), int(idle_seconds)


def _compute_top_apps(
    events: list[ActivityEvent], idle_periods: list[tuple[datetime, datetime]], limit: int = 10
) -> dict[str, int]:
    """Subtracts idle-overlap from each app-focus interval so leaving an app
    focused while away from the machine doesn't inflate its usage time."""
    focus_events = [e for e in events if e.event_type == "app_focus_change"]
    usage: dict[str, float] = defaultdict(float)

    for i, e in enumerate(focus_events):
        process = (e.details or {}).get("process", "unknown")
        next_ts = focus_events[i + 1].timestamp_utc if i + 1 < len(focus_events) else events[-1].timestamp_utc

        idle_overlap = sum(_overlap_seconds(e.timestamp_utc, next_ts, s, en) for s, en in idle_periods)
        duration = max(0.0, (next_ts - e.timestamp_utc).total_seconds() - idle_overlap)
        usage[process] += duration

    top = sorted(usage.items(), key=lambda kv: -kv[1])[:limit]
    return {process: int(seconds) for process, seconds in top}
