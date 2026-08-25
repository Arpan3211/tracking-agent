# Developer Guide — Running the Full Stack

This is the guide for standing up the whole system locally: PostgreSQL +
FastAPI backend (in Docker) and the React dashboard. For the Windows
monitoring agent itself (features, setup, anti-tamper service, browser
extension, report generation), see [AGENT.md](AGENT.md) — the agent is
optional here and works fine with zero backend involvement.

## Prerequisites

- **Docker Desktop** (Windows/Mac) or Docker Engine + Compose plugin
  (Linux) — the backend and its Postgres database both run in containers.
  On Windows, Docker Desktop must be **running** (its whale icon visible in
  the system tray) before any `docker` command will work — starting the
  app doesn't launch the engine for you. If `docker info` hangs or errors
  with `open //./pipe/docker_engine: The system cannot find the file
  specified`, Docker Desktop isn't running: launch it from the Start Menu
  and wait for the tray icon to stop animating (30–90s on a cold start)
  before retrying.
- **Node.js 20+** — for the frontend dashboard (Vite + React + TypeScript).
- Python and PostgreSQL do **not** need to be installed locally — both run
  inside the backend's containers.

## 1. Backend (FastAPI + PostgreSQL, in Docker)

```bash
cp backend/.env.example backend/.env
# edit backend/.env - set JWT_SECRET_KEY and SEED_ADMIN_PASSWORD at minimum.
# For local http:// (not https) dashboard dev, also set COOKIE_SECURE=false.

docker compose up -d db
docker compose build api
docker compose up -d api

docker compose exec api alembic upgrade head
docker compose exec api python -m scripts.seed_admin
```

Verify: `curl http://localhost:8000/health` → `{"status":"ok"}`. API docs
(Swagger UI) at `http://localhost:8000/docs`.

Note: `docker compose restart api` picks up code changes (the `backend/`
directory is volume-mounted into the container), but changes to
`backend/.env` require `docker compose up -d --force-recreate api` — a
plain restart reuses the container's already-baked-in environment.

To stop it: `docker compose stop api db` (keeps the containers and the
Postgres data volume, so `docker compose up -d db`/`api` resumes where you
left off). `docker compose down` also removes the containers but still
keeps the data volume; only `docker compose down -v` wipes the database,
and it does so with no confirmation prompt.

Full backend detail (test setup, architecture notes, manual ingestion via
curl) is in [backend/README.md](backend/README.md).

## 2. Dashboard (React + Vite)

```bash
cd frontend
npm install
npm run dev
```

Open `http://localhost:5173` and log in with the admin credentials from
`backend/.env` (`SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD`). The Vite dev
server proxies `/api` to `http://localhost:8000` (see
`frontend/vite.config.ts`) so the browser sees the dashboard and API as
one origin — this matters because auth uses httpOnly cookies with a CSRF
double-submit token, not a bearer token you'd attach manually.

For a production build: `npm run build` (outputs `frontend/dist/`, a
static bundle you can serve from any static host or reverse proxy alongside
the API — there's no dashboard-specific Docker service defined yet,
matching the original scope of the Compose file being API + DB only).

## 3. Point the agent at the backend (optional)

The agent works with no backend at all - it just queues events locally,
unsent - unless you set one environment variable. On the Windows machine
running `EmployeeAgent.exe`:

```powershell
setx EMPLOYEEAGENT_BACKEND_URL "http://<your-backend-host>:8000"
```

On the next agent restart, `SyncLoop` (see
`monitoring-agent/EmployeeAgent/Core/SyncLoop.cs`)
starts running: it enrolls the device with the backend on first sync
(`POST /api/v1/devices/enroll`), caches the returned API key locally, and
from then on drains its local SQLite event queue to
`POST /api/v1/ingest/events` every ~4 seconds whenever anything is pending -
small, frequent batches rather than one long-delayed periodic flush. A row
is deleted from the local queue only once the backend confirms it accepted
that batch, so a network outage just means the queue grows until
connectivity returns, not that events are lost.

If you're testing from a non-Windows machine, you can exercise ingestion
manually with `curl` — see
[backend/README.md](backend/README.md#pointing-a-test-agent-at-the-local-backend).

Agent build/run instructions, its own feature set, and standalone
(no-backend) usage are all in [AGENT.md](AGENT.md).

## Troubleshooting Docker on Windows

- **`error during connect: ... open //./pipe/docker_engine: The system
  cannot find the file specified`** — the Docker Desktop app isn't
  running. Start it (Start Menu → "Docker Desktop") and wait for the tray
  icon to finish its startup animation, then retry.
- **Docker Desktop installed but not in the usual `C:\Program Files\`
  location** — some installs are per-user, under
  `%LOCALAPPDATA%\Programs\DockerDesktop\Docker Desktop.exe`. If `docker`
  is on your `PATH` but nothing launches it automatically, start that exe
  directly.
- **WSL2 backend not installed/enabled** — Docker Desktop on Windows
  requires WSL2. If Docker Desktop's UI reports a WSL error on startup, run
  `wsl --install` (or `wsl --update`) in an elevated PowerShell, reboot, and
  retry.
- **Port already in use (`5432` or `8000`)** — something else on the host
  is already bound to Postgres' or the API's port. Stop the conflicting
  process, or change the left-hand side of the `ports:` mapping in
  `docker-compose.yml` (e.g. `"5433:5432"`).
- **`docker compose exec api alembic upgrade head` fails with a connection
  error** — the `api` container started before Postgres finished its own
  startup. The Compose file already waits on `db`'s healthcheck
  (`condition: service_healthy`), so this usually means `db` itself failed
  to start — check `docker compose logs db`.
- **Env var changes not taking effect** — see the note under step 1 above;
  `restart` isn't enough, use `docker compose up -d --force-recreate api`.
- **`alembic upgrade head` fails with `InvalidPasswordError: password
  authentication failed for user "postgres"`** — Postgres only applies
  `POSTGRES_PASSWORD` the first time it initializes an empty data volume;
  changing `backend/.env` afterward doesn't change the already-initialized
  database's real password, so they can drift apart (this is especially
  likely to bite you the first time you run this stack, if `backend/.env`
  didn't have its final password yet when `db` first started). Since this
  is local/dev data, the fix is to reset the volume so Postgres
  re-initializes with the current `backend/.env` values:
  ```bash
  docker compose down -v   # removes the db_data volume - local dev data only
  docker compose up -d db
  docker compose up -d api
  docker compose exec api alembic upgrade head
  docker compose exec api python -m scripts.seed_admin
  ```
