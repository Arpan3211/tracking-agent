import secrets

from fastapi import Cookie, Header, HTTPException, status

CSRF_COOKIE = "csrf_token"


def generate_csrf_token() -> str:
    return secrets.token_urlsafe(32)


async def verify_csrf(
    x_csrf_token: str | None = Header(default=None, alias="X-CSRF-Token"),
    csrf_token: str | None = Cookie(default=None),
) -> None:
    """Double-submit cookie CSRF check: the frontend reads the (non-httpOnly)
    csrf_token cookie via JS and echoes it back as a header on every
    state-changing request. A cross-site form/script can trigger the cookie
    to be sent automatically, but can't read its value to put in the header
    (that's the whole point of httpOnly-cookie auth needing this dance) -
    only same-origin JS can. Add as a dependency on any POST/PATCH/DELETE
    dashboard endpoint that relies on cookie auth; ingestion endpoints (API
    key auth) and login (no session to hijack yet) don't need it."""
    if not x_csrf_token or not csrf_token or not secrets.compare_digest(x_csrf_token, csrf_token):
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="CSRF token missing or invalid")
