import pytest_asyncio

from app.core.security import hash_password
from app.models.device import Device
from app.models.user import User, UserRole


@pytest_asyncio.fixture
async def rbac_fixture(db_session):
    supervisor = User(
        email="supervisor@example.com", hashed_password=hash_password("pw"), full_name="Supervisor",
        role=UserRole.supervisor,
    )
    db_session.add(supervisor)
    await db_session.flush()

    report = User(
        email="report@example.com", hashed_password=hash_password("pw"), full_name="Report",
        role=UserRole.employee, supervisor_id=supervisor.id,
    )
    other_employee = User(
        email="other@example.com", hashed_password=hash_password("pw"), full_name="Other", role=UserRole.employee,
    )
    hr = User(email="hr@example.com", hashed_password=hash_password("pw"), full_name="HR", role=UserRole.hr)
    db_session.add_all([report, other_employee, hr])
    await db_session.flush()

    report_device = Device(machine_name="REPORT-PC", api_key_hash="x", assigned_user_id=report.id)
    other_device = Device(machine_name="OTHER-PC", api_key_hash="y", assigned_user_id=other_employee.id)
    db_session.add_all([report_device, other_device])
    await db_session.commit()
    await db_session.refresh(report_device)
    await db_session.refresh(other_device)

    return {
        "supervisor": supervisor,
        "report": report,
        "other_employee": other_employee,
        "hr": hr,
        "report_device": report_device,
        "other_device": other_device,
    }


async def test_supervisor_sees_own_and_reports_devices_only(client, rbac_fixture):
    await client.post("/api/v1/auth/login", json={"email": "supervisor@example.com", "password": "pw"})
    response = await client.get("/api/v1/devices")
    assert response.status_code == 200
    machine_names = {d["machine_name"] for d in response.json()}
    assert machine_names == {"REPORT-PC"}


async def test_hr_sees_all_devices(client, rbac_fixture):
    await client.post("/api/v1/auth/login", json={"email": "hr@example.com", "password": "pw"})
    response = await client.get("/api/v1/devices")
    assert response.status_code == 200
    machine_names = {d["machine_name"] for d in response.json()}
    assert machine_names == {"REPORT-PC", "OTHER-PC"}


async def test_employee_cannot_access_other_employees_device(client, rbac_fixture):
    await client.post("/api/v1/auth/login", json={"email": "other@example.com", "password": "pw"})
    device_id = rbac_fixture["report_device"].id
    response = await client.get(f"/api/v1/devices/{device_id}/sessions")
    assert response.status_code == 403


async def test_employee_can_access_own_device(client, rbac_fixture):
    await client.post("/api/v1/auth/login", json={"email": "other@example.com", "password": "pw"})
    device_id = rbac_fixture["other_device"].id
    response = await client.get(f"/api/v1/devices/{device_id}/sessions")
    assert response.status_code == 200


async def test_supervisor_can_access_direct_reports_device(client, rbac_fixture):
    await client.post("/api/v1/auth/login", json={"email": "supervisor@example.com", "password": "pw"})
    device_id = rbac_fixture["report_device"].id
    response = await client.get(f"/api/v1/devices/{device_id}/sessions")
    assert response.status_code == 200


async def test_supervisor_cannot_access_unrelated_device(client, rbac_fixture):
    await client.post("/api/v1/auth/login", json={"email": "supervisor@example.com", "password": "pw"})
    device_id = rbac_fixture["other_device"].id
    response = await client.get(f"/api/v1/devices/{device_id}/sessions")
    assert response.status_code == 403


async def test_nonexistent_device_returns_404_not_403(client, rbac_fixture):
    await client.post("/api/v1/auth/login", json={"email": "other@example.com", "password": "pw"})
    response = await client.get("/api/v1/devices/00000000-0000-0000-0000-000000000000/sessions")
    assert response.status_code == 404


async def test_non_admin_cannot_access_admin_endpoints(client, rbac_fixture):
    await client.post("/api/v1/auth/login", json={"email": "supervisor@example.com", "password": "pw"})
    response = await client.get("/api/v1/admin/users")
    assert response.status_code == 403


async def test_hr_cannot_access_admin_endpoints(client, rbac_fixture):
    # HR sees the dashboard the same as Admin, but user/policy management is
    # Admin-only per app/api/v1/admin.py's decision (see backend README).
    await client.post("/api/v1/auth/login", json={"email": "hr@example.com", "password": "pw"})
    response = await client.get("/api/v1/admin/users")
    assert response.status_code == 403
