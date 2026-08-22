import logging
from email.message import EmailMessage

import aiosmtplib

from app.config import get_settings

logger = logging.getLogger(__name__)
settings = get_settings()


async def send_alert_email(to_address: str, device_name: str, message: str) -> None:
    email = EmailMessage()
    email["From"] = settings.smtp_from_address
    email["To"] = to_address
    email["Subject"] = f"Employee Agent Alert - {device_name}"
    email.set_content(message)

    try:
        await aiosmtplib.send(
            email,
            hostname=settings.smtp_host,
            port=settings.smtp_port,
            username=settings.smtp_username or None,
            password=settings.smtp_password or None,
            start_tls=settings.smtp_use_tls,
        )
    except Exception:
        # Alert emails are best-effort - the alert row itself is already
        # committed before this is called, so a down/misconfigured SMTP
        # server must never crash the scheduler job or lose the alert.
        logger.exception("Failed to send alert email to %s", to_address)
