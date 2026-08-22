import uuid

from fastapi import Cookie, Depends, Header, HTTPException, status
from jwt import PyJWTError
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.security import TokenType, decode_token, hash_api_key
from app.database import get_db
from app.models.device import Device
from app.models.user import User, UserRole


async def get_current_device(
    x_api_key: str = Header(..., alias="X-API-Key"),
    db: AsyncSession = Depends(get_db),
) -> Device:
    """Auth for agent->server ingestion calls: a per-device API key, not a
    user JWT (the agent runs unattended, there's no human to log in)."""
    key_hash = hash_api_key(x_api_key)
    result = await db.execute(select(Device).where(Device.api_key_hash == key_hash))
    device = result.scalar_one_or_none()
    if device is None:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid API key")
    return device


async def get_current_user(
    access_token: str | None = Cookie(default=None),
    db: AsyncSession = Depends(get_db),
) -> User:
    """Auth for dashboard calls: JWT access token in an httpOnly cookie (set
    by /auth/login and /auth/refresh)."""
    if access_token is None:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Not authenticated")

    try:
        payload = decode_token(access_token)
    except PyJWTError:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid or expired token")

    if payload.get("type") != TokenType.access:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Wrong token type")

    user = await db.get(User, uuid.UUID(payload["sub"]))
    if user is None or not user.is_active:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="User not found or inactive")

    return user


def require_roles(*roles: UserRole):
    """Dependency factory: require_roles(UserRole.hr, UserRole.admin) blocks
    anyone whose role isn't in the given set. Route-scoped RBAC (e.g. only
    HR/Admin can see the admin endpoints) is enforced this way; row-level
    scoping (e.g. a supervisor only sees their own reports' devices) is
    handled separately per-endpoint via app/core/rbac.py's scope helpers,
    since that depends on data, not just the caller's role."""

    async def _check(user: User = Depends(get_current_user)) -> User:
        if user.role not in roles:
            raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Insufficient permissions")
        return user

    return _check
