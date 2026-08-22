import logging

from apscheduler.schedulers.asyncio import AsyncIOScheduler

from app.config import get_settings
from app.services.aggregation import run_aggregation
from app.services.alerts_engine import run_alert_evaluation

logger = logging.getLogger(__name__)
settings = get_settings()

# APScheduler's AsyncIOScheduler runs in-process on the API's own event loop -
# sufficient at pilot scale per the original design call (see README). Split
# into a separate Celery+Redis worker service later only if the aggregation
# workload genuinely outgrows this.
scheduler = AsyncIOScheduler()


def start_scheduler() -> None:
    scheduler.add_job(run_aggregation, "interval", minutes=settings.aggregation_interval_minutes, id="aggregation")
    scheduler.add_job(
        run_alert_evaluation,
        "interval",
        minutes=settings.alert_evaluation_interval_minutes,
        id="alert_evaluation",
    )
    scheduler.start()
    logger.info(
        "Scheduler started: aggregation every %sm, alert evaluation every %sm",
        settings.aggregation_interval_minutes,
        settings.alert_evaluation_interval_minutes,
    )


def stop_scheduler() -> None:
    scheduler.shutdown(wait=False)
