import pytest_asyncio

from app.core.security import hash_password
from app.models.user import User, UserRole


@pytest_asyncio.fixture
async def admin_user(db_session):
    user = User(
        email="admin@example.com",
        hashed_password=hash_password("correct-password"),
        full_name="Test Admin",
        role=UserRole.admin,
    )
    db_session.add(user)
    await db_session.commit()
    await db_session.refresh(user)
    return user


async def test_login_success_sets_cookies(client, admin_user):
    response = await client.post(
        "/api/v1/auth/login", json={"email": "admin@example.com", "password": "correct-password"}
    )
    assert response.status_code == 200
    assert "access_token" in response.cookies
    assert "refresh_token" in response.cookies
    assert "csrf_token" in response.cookies


async def test_login_wrong_password_rejected(client, admin_user):
    response = await client.post("/api/v1/auth/login", json={"email": "admin@example.com", "password": "wrong"})
    assert response.status_code == 401


async def test_login_unknown_email_rejected(client):
    response = await client.post("/api/v1/auth/login", json={"email": "nobody@example.com", "password": "whatever"})
    assert response.status_code == 401


async def test_me_requires_authentication(client):
    response = await client.get("/api/v1/auth/me")
    assert response.status_code == 401


async def test_me_returns_current_user_after_login(client, admin_user):
    await client.post("/api/v1/auth/login", json={"email": "admin@example.com", "password": "correct-password"})
    response = await client.get("/api/v1/auth/me")
    assert response.status_code == 200
    assert response.json()["email"] == "admin@example.com"


async def test_logout_clears_session(client, admin_user):
    await client.post("/api/v1/auth/login", json={"email": "admin@example.com", "password": "correct-password"})
    logout_response = await client.post("/api/v1/auth/logout")
    assert logout_response.status_code == 204

    me_response = await client.get("/api/v1/auth/me")
    assert me_response.status_code == 401


async def test_inactive_user_cannot_login(client, db_session):
    user = User(
        email="inactive@example.com",
        hashed_password=hash_password("pw"),
        full_name="Inactive",
        role=UserRole.employee,
        is_active=False,
    )
    db_session.add(user)
    await db_session.commit()

    response = await client.post("/api/v1/auth/login", json={"email": "inactive@example.com", "password": "pw"})
    assert response.status_code == 401
