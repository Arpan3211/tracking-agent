# Employee Agent — Backend

FastAPI + PostgreSQL backend for the Employee Agent monitoring stack: receives
batched activity events from the Windows agent, aggregates them into sessions
and daily summaries, evaluates alert policies, and serves the dashboard API.

See the root [README.md](../README.md) for how this fits together with the
agent and (once built) the dashboard.

## Stack

FastAPI (async) · PostgreSQL via SQLAlchemy 2.0 (async) + Alembic · Pydantic
v2 · JWT auth (httpOnly cookies) via PyJWT · APScheduler (in-process) ·
Docker Compose.

## Local setup

From the repo root:

```bash
cp backend/.env.example backend/.env
# edit backend/.env - at minimum set JWT_SECRET_KEY and SEED_ADMIN_PASSWORD.
# For local http:// (not https) testing, also set COOKIE_SECURE=false -
# browsers/clients won't send Secure-flagged cookies over plain HTTP.

docker compose up -d db
docker compose build api
docker compose up -d api
```

Note: `docker compose restart api` picks up code changes (the backend
directory is volume-mounted), but env var changes in `backend/.env` require
`docker compose up -d --force-recreate api` - a restart alone reuses the
container's already-baked-in environment.

### Run migrations

```bash
docker compose exec api alembic upgrade head
```

To create a new migration after changing models in `app/models/`:

```bash
docker compose exec api alembic revision --autogenerate -m "describe the change"
docker compose exec api alembic upgrade head
```

### Seed the first Admin user

Uses `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD` / `SEED_ADMIN_FULL_NAME` from
`backend/.env`:

```bash
docker compose exec api python -m scripts.seed_admin
```

### Verify it's up

```bash
curl http://localhost:8000/health
# API docs (Swagger UI): http://localhost:8000/docs
```

## Running tests

Tests run against a **separate** database (`employee_agent_test`), created
once:

```bash
docker compose exec db psql -U postgres -c "CREATE DATABASE employee_agent_test;"
```

Then:

```bash
docker compose exec api pytest -v
```

Each test drops and recreates every table before it runs (see
`tests/conftest.py`) - slower than transaction-rollback isolation, but
correct given route handlers call `db.commit()` themselves, which would
otherwise defeat rollback-based isolation.

## Pointing a test agent at the local backend

The C# agent doesn't call this API yet - `ActivityLogger.cs`'s sync loop
(POSTs to `/api/v1/ingest/events`) is the next milestone (see root README).
To exercise ingestion manually in the meantime:

```bash
# 1. Enroll a device, get its API key
curl -X POST http://localhost:8000/api/v1/devices/enroll \
  -H "Content-Type: application/json" \
  -d '{"machine_name":"MY-TEST-PC","os_version":"Windows 11"}'

# 2. POST a batch of events using that key
curl -X POST http://localhost:8000/api/v1/ingest/events \
  -H "Content-Type: application/json" -H "X-API-Key: <api_key from step 1>" \
  -d '{"machine_name":"MY-TEST-PC","events":[
        {"event_type":"login","timestamp_utc":"2026-01-01T08:00:00Z","details":"someuser"}
      ]}'
```

## Architecture notes

- **Auth**: custom JWT (PyJWT), not `fastapi-users` - the `users` table's
  `role`/`supervisor_id` shape is bespoke enough that hand-rolled auth
  dependencies (`app/api/deps.py`) integrate more cleanly than fighting
  fastapi-users' own user-model abstraction. Access + refresh tokens live in
  httpOnly cookies; a third, deliberately non-httpOnly `csrf_token` cookie
  pairs with an `X-CSRF-Token` header (double-submit pattern, see
  `app/core/csrf.py`) to protect state-changing dashboard endpoints. The
  ingestion endpoints use a separate mechanism entirely: a per-device API key
  (`X-API-Key` header), since the agent runs unattended with no user session.
- **RBAC**: route-level gating (`require_roles` in `app/api/deps.py`, e.g.
  Admin-only on `/admin/*`) is separate from row-level scoping
  (`get_visible_user_ids` in `app/core/rbac.py`, e.g. a supervisor only
  seeing their direct reports' devices) - they compose, they're not the same
  mechanism.
- **Aggregation** (`app/services/aggregation.py`) fully recomputes
  `sessions` and `daily_activity_summary` per device on every scheduled run
  (default every 15 min, `AGGREGATION_INTERVAL_MINUTES`) rather than
  incrementally updating them. Delete-then-rebuild is trivially correct and
  cheap enough at pilot scale; revisit with a proper watermark/incremental
  approach if `activity_events` ever grows large enough for a full per-device
  scan to get slow. Its idle-aware app-usage math is deliberately kept in
  sync with `tools/generate_report.py`'s `compute_app_usage()` in the agent
  repo root - same idle-subtraction logic, so the dashboard and the
  standalone report script never disagree about what "active time" means.
- **Alerts** (`app/services/alerts_engine.py`) evaluate active policies
  every `ALERT_EVALUATION_INTERVAL_MINUTES` (default 5). Both rule types
  (`idle_threshold`, `late_login`) dedupe against re-firing on every tick by
  checking whether an alert already exists since the triggering event - not
  by tracking "already alerted" state elsewhere.
- **WebSocket** (`/api/v1/ws/alerts`) broadcasts to an in-process connection
  set (`app/api/v1/websocket.py`). This only works correctly with a single
  API replica; a multi-replica deployment would need a shared pub/sub
  (Postgres LISTEN/NOTIFY or Redis) instead.
- **Scheduler**: APScheduler running in-process on the API's own event loop,
  not a separate Celery+Redis worker - per the original design call, this is
  simpler and sufficient at pilot scale. Split it out if the aggregation
  workload ever genuinely outgrows it.
- **Admin endpoints are Admin-role-only**, not HR - user role/supervisor
  assignment and policy thresholds are more sensitive than the read-only
  dashboard scoping HR also gets. Flagged as a decision worth confirming, not
  an unambiguous spec requirement.
