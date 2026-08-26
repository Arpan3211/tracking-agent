# Employee Agent — Backend

## What this is

FastAPI + PostgreSQL backend for the Employee Agent monitoring stack: it
receives batched activity events from the Windows agent, aggregates them
into sessions and daily summaries, evaluates alert policies, and serves the
dashboard API (auth, devices, reports, alerts, admin).

See the root [README.md](../README.md) for how this fits together with the
agent and the dashboard, and [CODE_EXPLAINED.md](CODE_EXPLAINED.md) for a
file-by-file walkthrough of everything in this folder.

## Running it locally

Everything runs through Docker Compose, driven from the repo root (the
`docker-compose.yml` that defines `db` and `api` lives there, not in this
folder).

**1. Configure environment variables:**

```bash
cp backend/.env.example backend/.env
# edit backend/.env - at minimum set JWT_SECRET_KEY and SEED_ADMIN_PASSWORD.
# For local http:// (not https) testing, also set COOKIE_SECURE=false -
# browsers/clients won't send Secure-flagged cookies over plain HTTP.
```

**2. Start Postgres, then build and start the API:**

```bash
docker compose up -d db
docker compose build api
docker compose up -d api
```

**3. Run database migrations:**

```bash
docker compose exec api alembic upgrade head
```

**4. Seed the first Admin user** (uses `SEED_ADMIN_EMAIL` /
`SEED_ADMIN_PASSWORD` / `SEED_ADMIN_FULL_NAME` from `backend/.env`):

```bash
docker compose exec api python -m scripts.seed_admin
```

**5. Verify it's up:**

```bash
curl http://localhost:8000/health
# API docs (Swagger UI): http://localhost:8000/docs
```

**6. Stopping it:**

```bash
docker compose stop api db
```

This stops both containers but keeps them (and the Postgres data volume)
around, so `docker compose up -d db` / `docker compose up -d api` starts
right back up where you left off. If you want to remove the containers too
(not just stop them):

```bash
docker compose down
```

Postgres data still survives this - it's the named volume the compose file
gives `db`, not the container. Only add `-v` (`docker compose down -v`) if
you actually want to **wipe the database**, e.g. to start migrations over
from scratch; there's no confirmation prompt, so don't run it out of habit.

Once code changes, `docker compose restart api` picks them up (the backend
directory is volume-mounted into the container). Changes to `backend/.env`
need a harder reset: `docker compose up -d --force-recreate api` - a plain
restart reuses the container's already-baked-in environment variables.

To add a new migration after changing a model in `app/models/`:

```bash
docker compose exec api alembic revision --autogenerate -m "describe the change"
docker compose exec api alembic upgrade head
```

## Why it's set up to run this way locally

This is a **pilot-scale, single-machine deployment target**, not a
production cluster, and the local setup is deliberately built around that:

- **Docker Compose instead of a bare `uvicorn` process** so PostgreSQL comes
  up identically for every developer without anyone installing Postgres
  natively - the same `docker-compose.yml` at the repo root is the only
  thing standing between "clone the repo" and "have a working database,"
  which matters more here than in a team with a shared managed dev database.
- **The API container volume-mounts the `backend/` directory** rather than
  baking a fixed image per change, so `docker compose restart api` is enough
  to pick up code edits - no rebuild loop while iterating. `.env` values are
  still read once at container start, which is why they need
  `--force-recreate` instead of a restart; that split is a real gotcha
  worth knowing before you spend time debugging "my env var isn't taking
  effect."
- **Migrations and seeding are explicit manual steps**, not something that
  runs automatically on container start, so a failed migration or a bad seed
  doesn't silently break the `api` container's boot and mask the real error
  - you run `alembic upgrade head` and `seed_admin` yourself and see exactly
  what happened.
- **`COOKIE_SECURE=false` is an explicit opt-in**, not the default, because
  auth here uses httpOnly cookies that browsers refuse to send over plain
  `http://` when marked `Secure`. Production should never set this to
  `false`; it exists purely so `localhost` development (dashboard on
  `http://localhost:5173`, API on `http://localhost:8000`) works at all.
- **The scheduler (aggregation + alerts) runs in-process** inside the same
  `api` container rather than as a separate worker service, so there's
  nothing extra to start locally beyond `db` and `api` - see the Tech stack
  and Architecture notes below for why that's also fine at this scale in
  general, not just for local dev.

## Tech stack

FastAPI (async) · PostgreSQL via SQLAlchemy 2.0 (async) + Alembic · Pydantic
v2 · JWT auth (httpOnly cookies) via PyJWT · APScheduler (in-process) ·
Docker Compose.

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
  scan to get slow. Its idle-aware app-usage math (`compute_app_usage()`)
  subtracts idle-overlap from each app-focus interval, so leaving an app
  focused while away from the machine doesn't inflate its usage time.
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

## Deploying a pilot instance (free tier, for testing on real devices)

For trying the full flow (agent → backend → dashboard) on a handful of real
machines before committing to real hosting, this repo's `render.yaml`
deploys the backend to Render's free Web Service plan, which gives you a
working `https://<name>.onrender.com` URL with HTTPS already valid - no
domain purchase or reverse-proxy/TLS setup needed. The database is a
separate free [Neon](https://neon.tech) Postgres project, not Render's free
Postgres - Render's free Postgres auto-deletes itself after 30 days, Neon's
free tier doesn't expire.

**1. Create the database** - sign up at [neon.tech](https://neon.tech) (no
card required), create a project, and copy the connection string it gives
you. Rewrite it into SQLAlchemy's async form and add `?ssl=require`:

```text
postgresql+asyncpg://<user>:<password>@<your-project>.neon.tech/<dbname>?ssl=require
```

**2. Deploy the backend** - sign up at [render.com](https://render.com),
**New → Blueprint**, point it at this GitHub repo. Render reads
`render.yaml` at the repo root and provisions one free Web Service built
from `backend/Dockerfile`. It'll prompt for the env vars marked
`sync: false` in that file - paste in:

- `DATABASE_URL` - the Neon connection string from step 1
- `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD` - your first Admin login

Everything else (JWT secret, cookie/CORS settings, scheduler intervals) is
already set in `render.yaml`. On deploy, Render runs `alembic upgrade head`
and the idempotent `seed_admin` script automatically (via
`preDeployCommand`) before starting the service - no manual migration step
like the local Docker Compose flow above.

**3. Verify it's up:**

```bash
curl https://<your-service-name>.onrender.com/health
```

**4. Point the MSI at it** - use that same URL with
`monitoring-agent/install/build-installer.ps1`:

```powershell
.\build-installer.ps1 -BackendUrl "https://<your-service-name>.onrender.com" -Version "1.0.0"
```

**Known pilot-tier limits, not bugs:**

- The free Web Service spins down after 15 minutes idle and takes ~30-60s to
  wake on the next request - a device's first sync after a quiet period may
  be slow, not lost (the agent's local SQLite queue just holds events until
  the sync succeeds).
- Neon's free compute suspends after 5 minutes idle too, with a sub-second
  wake - not noticeable in practice.
- `CORS_ORIGINS` in `render.yaml` only allows `http://localhost:5173` (the
  local dashboard dev server) - update it to your deployed frontend's origin
  once you host the dashboard somewhere too, or dashboard logins will fail
  with a CORS error even though ingestion (which doesn't use CORS) keeps
  working.
- When you move to real hosting (e.g. Azure), swap `DATABASE_URL` to the new
  database, redeploy the container there, then rebuild the MSI with the new
  `-BackendUrl` and roll it out as a version bump - `Product.wxs`'s
  `MajorUpgrade` element already handles upgrading existing installs cleanly.

## Pointing a test agent at the local backend

The Windows agent's `SyncLoop` (see
`monitoring-agent/EmployeeAgent/Core/SyncLoop.cs`) already does this
automatically once `EMPLOYEEAGENT_BACKEND_URL` is set - it enrolls the
device, caches the API key, and batches events to `/api/v1/ingest/events`.
If you're developing from a non-Windows machine and don't have a real agent
to run, you can exercise the same ingestion flow manually:

```bash
# 1. Enroll a device, get its API key
curl -X POST http://localhost:8000/api/v1/devices/enroll \
  -H "Content-Type: application/json" \
  -d '{"machine_name":"MY-TEST-PC","os_version":"Windows 11"}'

# 2. POST a batch of events using that key
curl -X POST http://localhost:8000/api/v1/ingest/events \
  -H "Content-Type: application/json" -H "X-API-Key: <api_key from step 1>" \
  -d '{"machine_name":"MY-TEST-PC","events":[
        {"event_type":"login","timestamp_utc":"2026-01-01T08:00:00Z","details":{"username":"someuser"}}
      ]}'
```
