# Backend — Code Explained

A file-by-file walkthrough of everything under `backend/`. This covers what
each file does and why it's built the way it is — not a line-by-line
commentary. See [README.md](README.md) for setup/run instructions and the
higher-level architecture notes this doc expands on.

## App entry point & wiring

**`app/main.py`** — the FastAPI application object itself. Small on
purpose: it builds `app`, adds CORS middleware scoped to
`settings.cors_origins`, mounts the whole API under `/api/v1` via
`api_router` (see `app/api/v1/router.py`), and defines the one route that
lives here directly, `/health`. The `lifespan` context manager is what
starts and stops the in-process scheduler (`app/scheduler.py`) alongside
the app's own process lifetime — there's no separate worker process to
start.

**`app/config.py`** — a single `Settings` (Pydantic `BaseSettings`) class
that is the *only* place environment variables get read in the whole
backend. Everything from the database URL to JWT expiry to SMTP credentials
to scheduler intervals lives here with typed defaults, loaded from
`backend/.env` (see `backend/.env.example`). `get_settings()` is
`@lru_cache`d so the whole app shares one parsed instance instead of
re-reading and re-validating the environment on every call.

**`app/database.py`** — creates the one shared async SQLAlchemy engine and
session factory (`create_async_engine` / `async_sessionmaker`), defines the
`Base` every model inherits from, and `get_db()`, the FastAPI dependency
every route uses to get a request-scoped `AsyncSession`. `pool_pre_ping=True`
means a stale/dropped connection (e.g. after Postgres restarts) gets
detected and replaced rather than surfacing as a confusing query failure.

**`app/scheduler.py`** — wraps APScheduler's `AsyncIOScheduler`, running
**in-process on the API's own event loop** rather than as a separate
worker. `start_scheduler()` registers exactly two recurring jobs —
`run_aggregation` (from `app/services/aggregation.py`) and
`run_alert_evaluation` (from `app/services/alerts_engine.py`) — each on its
own configurable interval. This is a deliberate simplicity choice for pilot
scale, documented right in the file: split into a real Celery+Redis worker
only if the aggregation workload ever genuinely outgrows a single process.

## `app/core/` — cross-cutting security/auth primitives

**`app/core/security.py`** — every password/token primitive in the
backend, in one place: `hash_password`/`verify_password` (bcrypt via
`passlib`), `generate_api_key`/`hash_api_key` (device ingestion keys — uses
plain SHA-256, not bcrypt, since a random high-entropy token doesn't need
bcrypt's deliberate slowness), and `create_access_token`/
`create_refresh_token`/`decode_token` (PyJWT). `decode_token` intentionally
lets `jwt.PyJWTError` propagate rather than swallowing it — callers (in
`app/api/deps.py` and `app/api/v1/auth.py`) are the ones who decide that
means a 401.

**`app/core/rbac.py`** — one function, `get_visible_user_ids()`, and the
entire row-level scoping model in the backend: HR/Admin get `None` (no
restriction), a supervisor gets themself plus their direct reports (one
level, not a full org-chart rollup), and everyone else gets just
themselves. Every device/alert query in `app/api/v1/devices.py` and
`alerts.py` filters through this — it's what makes "a supervisor only sees
their reports' devices" a single reusable rule instead of duplicated
per-endpoint logic.

**`app/core/csrf.py`** — implements the double-submit cookie pattern:
`generate_csrf_token()` makes the random token set as a cookie at login,
and `verify_csrf` (a FastAPI dependency) checks that the same value comes
back as an `X-CSRF-Token` header on state-changing requests. This only
works because the `csrf_token` cookie is deliberately *not* httpOnly (see
`_set_auth_cookies` in `auth.py`) — only same-origin JS can read a cookie
to echo it back as a header, so this defeats cross-site form/script
submissions even though they can still trigger the cookie to be sent
automatically.

## `app/api/deps.py` — shared FastAPI dependencies

Three dependencies used across nearly every route: `get_current_device`
(API-key auth for the agent's ingestion calls — unattended process, no user
session, so it's a device-scoped key rather than a JWT), `get_current_user`
(cookie-based JWT auth for dashboard calls, validating the token type is
specifically `access` and that the user still exists and is active), and
`require_roles(*roles)` (a dependency *factory* — `require_roles(UserRole.admin)`
returns a dependency that 403s anyone whose role isn't in the given set).
This file draws the explicit line the rest of the backend follows:
route-level gating (who's *allowed to call this endpoint at all*) lives
here via `require_roles`; row-level scoping (*which rows* they see once
they're in) lives in `app/core/rbac.py` — they compose rather than being
the same mechanism.

## `app/api/v1/` — routes

**`router.py`** — just assembles every sub-router
(`ingest`/`auth`/`devices`/`alerts`/`reports`/`admin`/`websocket`) into one
`api_router`, mounted under `/api/v1` by `main.py`. No logic of its own.

**`ingest.py`** — the two endpoints the agent actually calls:
`POST /devices/enroll` (creates a `Device` row if `machine_name` is new, or
re-issues a fresh API key if it already exists — re-enrolling silently
invalidates the old key rather than erroring, which is what lets a
reinstalled agent recover on its own) and `POST /ingest/events` (bulk
inserts a batch of events for the authenticated device, storing each
event's `details` straight into `ActivityEvent.details` (JSONB) — the agent
sends an already-structured object per event type, so there's no
server-side parsing step). Guards that `payload.machine_name` matches the
device the API key actually belongs to, so one device's key can't be used
to submit events under another device's name.

**`auth.py`** — login/refresh/logout/me. `_set_auth_cookies()` is the one
function that sets all three cookies together (access token, refresh
token, CSRF token) with carefully different scopes: the refresh token is
path-scoped to `/api/v1/auth` only, so it's never sent on — and can't leak
via — ordinary dashboard API calls; the CSRF cookie is the one deliberately
non-httpOnly cookie, for the reason described in `core/csrf.py` above.
`/refresh` mints a whole new set of cookies (not just a new access token),
so a refresh also rotates the CSRF token.

**`devices.py`** — device listing and per-device drill-down
(sessions/activity-summary/idle-periods/paginated events).
`get_accessible_device()` is the shared row-level authorization dependency
nearly every route in this file (and `reports.py`) depends on: 404 if the
device genuinely doesn't exist, 403 if it exists but isn't in the caller's
visible set from `get_visible_user_ids()` — checked in that order
deliberately, so an unauthorized caller can't distinguish "doesn't exist"
from "exists but not yours" by response code alone. `get_device_events` is
the one paginated endpoint (`Page[ActivityEventOut]`, see
`schemas/pagination.py`), since raw event history can be arbitrarily large;
sessions, daily summaries, and idle periods are small enough to return as
plain lists. `get_idle_periods` pairs raw `idle_start`/`idle_end` events
into `(start, end, duration)` on the fly, computed per-request rather than
stored — it's the per-pause breakdown behind
`DailyActivitySummary.total_idle_seconds`, which only has each day's total,
not the individual pauses that made it up; a trailing unmatched
`idle_start` is returned as an ongoing period (`end: null`), the same
open-session convention `get_device_sessions` uses for a session with no
`logout_at` yet.

**`alerts.py`** — lists alerts (scoped through the same
`get_visible_user_ids`, joined against `Device` since `Alert` has no
`assigned_user_id` of its own) and acknowledges one
(`POST /{alert_id}/acknowledge`, CSRF-protected since it's a mutation
authenticated by cookie).

**`reports.py`** — the one export endpoint, `GET /reports/export`. Reuses
`devices.py`'s `get_accessible_device` dependency for the same
authorization even though this route's own path has no `{device_id}`
segment — FastAPI resolves it as a query parameter instead, so the same
row-level check is enforced without duplicating it. Every export is
recorded via `record_audit()` before streaming the file back, since pulling
raw activity data out of the system is exactly the kind of action the
compliance audit trail exists to capture.

**`websocket.py`** — the live alert feed. `AlertConnectionManager` is a
process-local `set` of connected `WebSocket`s with a `broadcast()` that
fans a payload out to all of them, quietly dropping any connection that
errors out mid-send. The websocket handshake itself re-implements the same
cookie-based JWT check `get_current_user` does for REST routes (a
`WebSocket` doesn't go through the normal `Depends()` dependency chain the
same way), closing with code `1008` (policy violation) on any auth
failure. The module docstring/comment is explicit that this
process-local singleton only works correctly with a single API replica —
a multi-replica deployment would need every replica to hear about every
alert via a shared pub/sub (Postgres LISTEN/NOTIFY or Redis) instead.

**`admin.py`** — every route here carries
`dependencies=[Depends(require_roles(UserRole.admin))]` at the *router*
level, so the whole file is Admin-only in one place rather than repeated
per-route. Covers user CRUD (create/update, including re-hashing the
password only when one is actually provided in a `PATCH`), policy CRUD
(the alert-rule definitions `alerts_engine.py` evaluates), and reading the
audit log. Every mutating route additionally depends on `verify_csrf` and
calls `record_audit()` before committing.

## `app/models/` — SQLAlchemy ORM tables

**`user.py`** — the `users` table and `UserRole` enum
(`employee`/`supervisor`/`hr`/`admin`). The self-referential
`supervisor_id` FK plus the `supervisor`/`direct_reports` relationship pair
is what makes the one-level org-chart lookup in `core/rbac.py` possible.

**`device.py`** — one row per enrolled machine. Stores only
`api_key_hash`, never the plaintext key — the same principle as a user
password, so a database leak alone can't be used to impersonate a device's
ingestion traffic.

**`activity_event.py`** — the highest-volume table by far: every raw event
the agent ever sends. `details` (JSONB) holds the agent's structured
per-event-type payload exactly as sent — there's no separate raw/parsed
split anymore since nothing on the backend parses it. Carries the two
indexes that matter most given the query patterns above it — `(device_id,
timestamp_utc)` for every per-device time-range query, and `event_type`
alone for the aggregation job's event-type filters.

**`session.py`** — `DeviceSession` (table name still `sessions` — the
Python class is renamed only to avoid colliding with SQLAlchemy's own
`Session` class). Purely derived data: never written by the ingestion
endpoint, only ever rebuilt wholesale by
`aggregation.py`'s `_rebuild_sessions()` from raw login/logout events.

**`daily_activity_summary.py`** — the pre-aggregated per-device-per-day
rollup (`total_active_seconds`, `total_idle_seconds`, `top_apps` as JSONB,
`event_count`) that lets dashboard queries avoid scanning raw
`activity_events` every time. Unique on `(device_id, date)` — the
aggregation job upserts into this rather than always inserting.

**`alert.py`** — one row per fired alert: which `Policy` triggered it,
which `Device`, the rendered message, and read/acknowledge state.

**`policy.py`** — the alert *rule definitions* HR/Admin configure
(`PolicyRuleType.idle_threshold` or `.late_login`), each with a single
`threshold_value` integer whose meaning depends on the rule type (documented
directly on the class: minutes of idle time for one, minutes-since-midnight
UTC cutoff for the other) — evaluated on a schedule by `alerts_engine.py`.

**`audit_log.py`** — one row per sensitive admin/HR action (user changes,
policy changes, report exports), written by `services/audit.py`'s
`record_audit()` from inside the relevant endpoint rather than by a
database trigger — a compliance requirement once real employee data is
involved.

## `app/schemas/` — Pydantic request/response shapes

These mirror the models above but exist as a separate layer on purpose —
an ORM model and its API-facing shape are different concerns (a model
never exposes `hashed_password`, an `*Out` schema never accepts one). Every
`*Out` schema sets `model_config = ConfigDict(from_attributes=True)` so it
can be built directly from an ORM instance (`Model.model_validate(obj)` /
FastAPI's `response_model`) rather than needing a manual field-by-field
mapping. One file per resource: `user.py` (`UserOut`/`UserCreate`/
`UserUpdate`), `device.py` (enroll request/response plus `DeviceOut` —
note `DeviceEnrollResponse.api_key` is the one place a plaintext secret
ever appears in a response, by design, once), `activity.py`
(`IngestRequest`/`IngestResponse`, the `*Out` read shapes, and
`IdlePeriodOut` — `IngestRequest.events` is capped at 500 items via
`Field(max_length=500)` so one oversized payload can't force a huge single
insert transaction), `session.py` (`SessionOut`), `alert.py` (`AlertOut`), `policy.py`
(`PolicyCreate`/`PolicyUpdate`/`PolicyOut`), `audit_log.py`
(`AuditLogOut`), `auth.py` (just `LoginRequest`), and `pagination.py`
(`Page[T]`, a generic envelope reused by every paginated endpoint —
currently only `GET /devices/{id}/events`).

## `app/services/` — business logic, independent of any one route

**`aggregation.py`** — the scheduled job wired up in `scheduler.py`.
`run_aggregation()` fully **deletes and rebuilds** every device's
`sessions` and `daily_activity_summary` rows from complete event history on
every run, rather than incrementally updating them — documented as a
deliberate pilot-scale simplicity trade-off (`_rebuild_sessions` pairs up
login/logout events in timestamp order; a trailing unmatched login becomes
an open session with no `logout_at`). `_compute_top_apps()` is the
idle-aware app-usage calculation: for each `app_focus_change` interval, it
subtracts any overlap with idle periods (from `_compute_idle_periods()`,
built off `idle_start`/`idle_end` events) before accumulating duration —
the same idle-subtraction principle the rest of the docs reference — so
leaving an app focused while away from the keyboard doesn't inflate its
usage numbers.

**`alerts_engine.py`** — the other scheduled job. Loads every active
`Policy` and dispatches on `rule_type`: `_evaluate_idle_threshold()` checks
each device's *latest* idle_start/idle_end event — if it's currently mid-
idle-period and has been for longer than the policy's threshold, and no
alert already exists for that same idle period (`_alert_exists_since`,
which is what prevents re-firing every single evaluation tick), it fires.
`_evaluate_late_login()` only scans a short recent window (2× the
evaluation interval) rather than all history, since it only needs to catch
logins that *just* happened. `_create_alert()` is the single point where a
firing turns into three things at once: an `Alert` row, a live broadcast
through `websocket.py`'s `alert_manager`, and (if the device has an
assigned user with a supervisor) a best-effort email via `email.py`.

**`email.py`** — a thin `aiosmtplib` wrapper, `send_alert_email()`. Any
exception is caught and logged, never re-raised — a down/misconfigured SMTP
server must not crash the scheduler job or roll back the alert that was
already committed before this gets called.

**`export.py`** — turns a list of `ActivityEvent` rows into either a CSV
(stdlib `csv` module) or an `.xlsx` workbook (`openpyxl`) in memory,
returning a `BytesIO` either way so `reports.py` can stream it straight
back without touching disk. `_format_details()` renders each row's
structured `details` dict back into a readable `"key=value; key2=value2"`
single string for the export column, since a spreadsheet cell isn't the
place for nested JSON.

**`audit.py`** — one function, `record_audit()`. Deliberately doesn't call
`db.commit()` itself — it just `db.add()`s the `AuditLog` row onto the
caller's existing session, so the caller's own commit (immediately after
their real write) covers both atomically. An audit entry can never exist
without the action it records actually having happened, or vice versa.

## `alembic/` — database migrations

**`env.py`** — Alembic's async-engine bootstrap: it imports `app.models`
purely for its side effect of registering every model class on
`Base.metadata` (hence the `# noqa: F401` — the import itself is the point,
nothing in it is used by name), points Alembic's connection string at
`settings.database_url` from the same `app/config.py` the app uses, and
runs migrations through `async_engine_from_config` so it works against the
same `asyncpg` driver the app does. Supports both `--sql` (offline,
generate raw SQL) and normal online migration modes.

**`script.py.mako`** — the template new migration files are generated
from; not itself a migration.

**`versions/ae41ff2bc6af_initial_schema.py`** — the first migration: creates
every table described under `app/models/` above, in one shot, auto-generated
by `alembic revision --autogenerate` from the models as they existed at
that point (it still has separate `details_raw`/`details_parsed` columns on
`activity_events`, from before the agent sent structured payloads).

**`versions/ae3336a9cda1_structured_event_details.py`** — collapses that
split into the single `details` (JSONB) column `app/models/activity_event.py`
now defines: drops `details_raw` and renames `details_parsed` → `details`,
matching the agent no longer sending a delimited string that needed
parsing on arrival.

## `scripts/seed_admin.py`

Creates the very first Admin user from `SEED_ADMIN_EMAIL`/
`SEED_ADMIN_PASSWORD`/`SEED_ADMIN_FULL_NAME` env vars — the only way to get
an account into an otherwise-empty database, since every other user is
created *through* the admin API, which needs an authenticated Admin to call
it. Idempotent: running it again when that email already exists just
prints a message and exits rather than erroring or creating a duplicate.
Meant to run as a module (`python -m scripts.seed_admin`) inside the `api`
container specifically so its `app.*` imports resolve.

## `tests/`

**`conftest.py`** — the shared pytest fixtures every test file uses:
`db_session` creates a **fresh SQLAlchemy engine per test** (not shared at
module scope) against a separate `employee_agent_test` database, drops and
recreates every table before yielding a session — deliberately not
transaction-rollback isolation, because route handlers call `db.commit()`
themselves, which would defeat a rollback-based approach. The per-test
engine is itself a deliberate fix for an async-specific footgun documented
right in the fixture: a shared engine's connection pool ends up bound to
whichever test's event loop created it first, and every other test then
fails with "attached to a different loop." `client` wraps that session in
an `httpx.AsyncClient` pointed straight at the FastAPI `app` via
`ASGITransport` (no real HTTP server involved), overriding `get_db` so
every request in a test hits the same isolated session.

**`test_auth.py`** — login/refresh/logout/`/me` behavior: correct
credentials set cookies, wrong password and unknown email are both
rejected the same way (401, not a distinguishing error), `/me` requires
authentication, logout actually clears the session, and an `is_active=False`
user can't log in even with the right password.

**`test_ingest.py`** — the agent-facing endpoints: enrollment returns a
usable API key, a valid key can post events successfully, an invalid key is
rejected, a `machine_name` that doesn't match the key's own device is
rejected, and re-enrolling an already-known machine issues a working new
key (implicitly proving the old one is invalidated).

**`test_rbac.py`** — the row-level and role-level authorization matrix:
supervisors see only themselves + their direct reports' devices, HR sees
everything, an employee can reach their own device but not another
employee's (403), a nonexistent device is a 404 even for an otherwise
unauthorized caller (never a 403, so existence can't be inferred), and
non-Admin/non-HR roles can't reach `/admin/*` at all.
