# Employee Agent

A three-part employee monitoring stack: a Windows agent that captures activity
locally and syncs it to a backend, a FastAPI + PostgreSQL backend that
ingests and aggregates that activity, and a React dashboard for
supervisors/HR/Admin to view it.

```
frontend/                        React + TypeScript dashboard (Vite)
backend/                         FastAPI + PostgreSQL API (ingestion, dashboard, alerts)
monitoring-agent/
  EmployeeAgent/                 per-user-session Windows agent (WinForms, no visible window)
  EmployeeAgent.Service/         Windows Service - OS-enforced anti-tamper supervisor
  EmployeeAgent.NativeHost/      Chrome/Edge native messaging host - full URL tracking
  browser-extension/             Manifest V3 extension - reports tab URLs to the native host
  install/                       PowerShell scripts (Windows Service + native host registration)
```

The agent works standalone (queuing events locally in SQLite) even with no
backend running, but activity is only visible in the dashboard once the
agent is synced to a running backend, at which point events stream over in
small, frequent batches — the sections below just point you to the right
guide for what you're doing.

## Where to go

- **Setting up the full stack for development** (backend + Docker +
  PostgreSQL + dashboard, and wiring the agent to sync to it) →
  [DEVELOPMENT.md](DEVELOPMENT.md)
- **The Windows monitoring agent itself** (features, standalone setup,
  anti-tamper service, browser extension, multi-laptop pilot mode, known
  limitations) → [AGENT.md](AGENT.md)
- **Backend architecture and API details** (auth, RBAC, aggregation,
  alerts, testing) → [backend/README.md](backend/README.md)
- **Frontend architecture and dev server details** (routing, auth flow,
  API client, why it runs outside Docker) → [frontend/README.md](frontend/README.md)

## What's next

The core loop (agent → backend → dashboard) is complete and working
end-to-end. Reasonable next steps, roughly in priority order:

- **Production security hardening**: encrypt the local SQLite event queue
  and screenshots at rest, add field-level encryption for sensitive `activity_events`
  columns in Postgres, and configure the Group Policy restriction on who
  can stop `EmployeeAgentService` (see [AGENT.md](AGENT.md)).
- **Confirm the HR-vs-Admin policy management decision** — `/admin/*` is
  currently Admin-only; flag if HR should manage alert policies too.
- **PDF export**, if actually needed beyond CSV/XLSX.
- **A shared pub/sub for the WebSocket alert broadcast** (Postgres
  LISTEN/NOTIFY or Redis) if the API ever needs to run as more than one
  replica.
- **Dashboard test coverage** (component/integration tests) — the backend
  has pytest coverage for ingestion/auth/RBAC; the dashboard doesn't yet
  have an equivalent.
- **Firefox support** for full-URL tracking, if needed.
