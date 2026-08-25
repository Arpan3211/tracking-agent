async def test_enroll_device_returns_api_key(client):
    response = await client.post(
        "/api/v1/devices/enroll", json={"machine_name": "PYTEST-PC", "os_version": "Windows 11"}
    )
    assert response.status_code == 201
    body = response.json()
    assert body["machine_name"] == "PYTEST-PC"
    assert len(body["api_key"]) > 20


async def test_ingest_events_with_valid_key(client):
    enroll = await client.post("/api/v1/devices/enroll", json={"machine_name": "PYTEST-PC"})
    api_key = enroll.json()["api_key"]

    response = await client.post(
        "/api/v1/ingest/events",
        headers={"X-API-Key": api_key},
        json={
            "machine_name": "PYTEST-PC",
            "events": [
                {"event_type": "login", "timestamp_utc": "2026-01-01T08:00:00Z", "details": {"username": "someuser"}},
                {
                    "event_type": "app_focus_change",
                    "timestamp_utc": "2026-01-01T08:01:00Z",
                    "details": {"process": "chrome", "title": "Test"},
                },
            ],
        },
    )
    assert response.status_code == 200
    assert response.json()["accepted"] == 2


async def test_ingest_events_rejects_invalid_api_key(client):
    response = await client.post(
        "/api/v1/ingest/events",
        headers={"X-API-Key": "totally-bogus-key"},
        json={
            "machine_name": "PYTEST-PC",
            "events": [{"event_type": "login", "timestamp_utc": "2026-01-01T08:00:00Z"}],
        },
    )
    assert response.status_code == 401


async def test_ingest_events_rejects_machine_name_mismatch(client):
    enroll = await client.post("/api/v1/devices/enroll", json={"machine_name": "PYTEST-PC"})
    api_key = enroll.json()["api_key"]

    response = await client.post(
        "/api/v1/ingest/events",
        headers={"X-API-Key": api_key},
        json={
            "machine_name": "SOME-OTHER-PC",
            "events": [{"event_type": "login", "timestamp_utc": "2026-01-01T08:00:00Z"}],
        },
    )
    assert response.status_code == 400


async def test_re_enrolling_existing_machine_issues_new_key(client):
    first = await client.post("/api/v1/devices/enroll", json={"machine_name": "PYTEST-PC"})
    second = await client.post("/api/v1/devices/enroll", json={"machine_name": "PYTEST-PC"})

    assert first.json()["device_id"] == second.json()["device_id"]
    assert first.json()["api_key"] != second.json()["api_key"]

    # The old key must no longer work after re-enrollment.
    old_key_response = await client.post(
        "/api/v1/ingest/events",
        headers={"X-API-Key": first.json()["api_key"]},
        json={
            "machine_name": "PYTEST-PC",
            "events": [{"event_type": "login", "timestamp_utc": "2026-01-01T08:00:00Z"}],
        },
    )
    assert old_key_response.status_code == 401
