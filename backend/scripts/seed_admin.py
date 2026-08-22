"""Creates the first Admin user from SEED_ADMIN_* env vars (see .env.example).

Run from /app inside the container as a module (not a bare script) so `app.*`
imports resolve correctly:

    docker compose exec api python -m scripts.seed_admin
"""

import asyncio
import os

from sqlalchemy import select

from app.core.security import hash_password
from app.database import AsyncSessionLocal
from app.models.user import User, UserRole


async def seed_admin() -> None:
    email = os.environ.get("SEED_ADMIN_EMAIL", "admin@example.com")
    password = os.environ.get("SEED_ADMIN_PASSWORD")
    full_name = os.environ.get("SEED_ADMIN_FULL_NAME", "Admin User")

    if not password:
        raise SystemExit("SEED_ADMIN_PASSWORD must be set (see .env.example)")

    async with AsyncSessionLocal() as db:
        result = await db.execute(select(User).where(User.email == email))
        existing = result.scalar_one_or_none()
        if existing is not None:
            print(f"Admin user {email} already exists - skipping.")
            return

        admin = User(
            email=email,
            hashed_password=hash_password(password),
            full_name=full_name,
            role=UserRole.admin,
            is_active=True,
        )
        db.add(admin)
        await db.commit()
        print(f"Created admin user: {email}")


if __name__ == "__main__":
    asyncio.run(seed_admin())
