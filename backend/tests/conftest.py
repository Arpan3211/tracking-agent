import os

import pytest_asyncio
from httpx import ASGITransport, AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine

from app.database import Base, get_db
from app.main import app

# Separate database from the dev one (employee_agent) so tests never touch
# real/manually-entered data. Must exist already - see backend README's
# "running tests" section for the one-time createdb command.
TEST_DATABASE_URL = os.environ.get(
    "TEST_DATABASE_URL", "postgresql+asyncpg://postgres:postgres@db:5432/employee_agent_test"
)


@pytest_asyncio.fixture
async def db_session():
    """Creates a fresh engine PER TEST rather than once at module scope.
    pytest-asyncio gives each test its own event loop by default, and
    asyncpg connections are bound to the loop they were created on - a
    shared module-level engine's pool ends up attached to whichever test
    happened to run first, and every other test then fails with
    'another operation is in progress' / 'attached to a different loop'.
    A fresh engine per test costs a bit of connection-setup overhead but
    sidesteps that entirely; fine at this suite's size."""
    engine = create_async_engine(TEST_DATABASE_URL)
    try:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.drop_all)
            await conn.run_sync(Base.metadata.create_all)

        session_factory = async_sessionmaker(engine, expire_on_commit=False, class_=AsyncSession)
        async with session_factory() as session:
            yield session
    finally:
        await engine.dispose()


@pytest_asyncio.fixture
async def client(db_session):
    async def override_get_db():
        yield db_session

    app.dependency_overrides[get_db] = override_get_db
    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as ac:
        yield ac
    app.dependency_overrides.clear()
