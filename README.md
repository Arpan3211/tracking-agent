# Employee Agent

A three-part employee monitoring stack: a Windows agent that captures activity
locally and syncs it to a backend, a FastAPI + PostgreSQL backend that
ingests and aggregates that activity, and a React dashboard for
supervisors/HR/Admin to view it.

```
EmployeeAgent/            per-user-session Windows agent (WinForms, no visible window)
EmployeeAgent.Service/    Windows Service - OS-enforced anti-tamper supervisor
EmployeeAgent.NativeHost/ Chrome/Edge native messaging host - full URL tracking
browser-extension/        Manifest V3 extension - reports tab URLs to the native host
backend/                  FastAPI + PostgreSQL API (ingestion, dashboard, alerts)
dashboard/                React + TypeScript dashboard (Vite)
tools/                    generate_report.py - standalone log-to-Markdown report
install/                  PowerShell scripts (Windows Service + native host registration)
```

The agent still works entirely standalone (writing to a local log file and,
optionally, generating a Markdown report via `tools/generate_report.py`) if
you never stand up the backend — see "Running without a backend" below. The
sections below assume you want the full stack.

## Running the whole stack locally

### 1. Backend (FastAPI + PostgreSQL, in Docker)

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

Verify: `curl http://localhost:8000/health` → `{"status":"ok"}`. Full detail
(migrations, tests, architecture notes) in [backend/README.md](backend/README.md).

### 2. Dashboard (React + Vite)

```bash
cd dashboard
npm install
npm run dev
```

Open `http://localhost:5173` and log in with the admin credentials from
`backend/.env` (`SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD`). The Vite dev
server proxies `/api` to `http://localhost:8000` (see `dashboard/vite.config.ts`)
so the browser sees the dashboard and API as one origin — this matters
because auth uses httpOnly cookies with a CSRF double-submit token, not a
bearer token you'd attach manually.

For a production build: `npm run build` (outputs `dashboard/dist/`, a static
bundle you can serve from any static host or reverse proxy alongside the API
— there's no dashboard-specific Docker service defined yet, matching the
original scope of the Compose file being API + DB only).

### 3. Point the agent at the backend

The agent works exactly as before (local-log-only) unless you set one
environment variable. On the Windows machine running `EmployeeAgent.exe`:

```powershell
setx EMPLOYEEAGENT_BACKEND_URL "http://<your-backend-host>:8000"
```

On the next agent restart, `SyncLoop` (see `EmployeeAgent/Core/SyncLoop.cs`)
starts running: it enrolls the device with the backend on first sync
(`POST /api/v1/devices/enroll`), caches the returned API key locally, and
from then on batches unsent lines from the local activity log to
`POST /api/v1/ingest/events` every 30 seconds or every 50 events, whichever
comes first. The local log file is never deleted or truncated — it's the
offline-resilience buffer, so a network outage just means the backend falls
behind, not that events are lost. A "last synced line" pointer file
(`sync_state_<machine>.json`, next to the log) makes this resume correctly
across agent restarts instead of resending or dropping lines.

If you're testing from a non-Windows machine (like this dev environment),
you can exercise ingestion manually with `curl` — see
[backend/README.md](backend/README.md#pointing-a-test-agent-at-the-local-backend).

## Running without a backend

The agent, its Windows Service supervisor, the browser extension, and
`tools/generate_report.py` all work exactly as documented below with zero
backend involvement — this was the whole point of keeping the local
JSON-lines log as the write-ahead buffer rather than making the backend a
hard dependency.

## What's implemented (agent)

| Feature | Where | Notes |
|---|---|---|
| Login/Logout | `Core/AgentContext.cs` (`SystemEvents.SessionSwitch`) | Also captures lock/unlock, remote connect/disconnect |
| Idle/Active detection | `Core/IdleTimeMonitor.cs` | 5-minute threshold (configurable) |
| Active window & app tracking | `Core/WindowActivityMonitor.cs` | Polls every 3s, logs only on change |
| App usage duration | `tools/generate_report.py` (`compute_app_usage`) | Derived from consecutive focus-change timestamps, with idle time subtracted out so time-away-with-an-app-focused isn't counted as usage |
| Website tracking (domain-level) | `Core/WindowActivityMonitor.cs` | Best-effort regex fallback on browser window titles — used only when the browser extension isn't installed |
| Website tracking (full URL) | `browser-extension/` + `EmployeeAgent.NativeHost/` | Manifest V3 extension reports every tab navigation to a Native Messaging host, which writes `website_visited` events straight to the shared log. Chrome/Edge only — Firefox not implemented |
| File activity | `Core/FileActivityMonitor.cs` | `FileSystemWatcher` for create/rename/change/delete + ETW (`Microsoft-Windows-Kernel-File`) for real **open** events, all scoped to Desktop, Documents, Downloads |
| Screenshot capture | `Core/ScreenshotCapture.cs` | Every 10 minutes by default, one PNG per connected monitor |
| Network/bandwidth usage | `Core/NetworkUsageMonitor.cs` | Per-process, via ETW (`Microsoft-Windows-Kernel-Network`) — not just whole-machine totals |
| USB/device activity | `Core/DeviceActivityMonitor.cs` | Via WMI `Win32_DeviceChangeEvent` |
| Printer usage | `Core/PrinterActivityMonitor.cs` | Polls WMI `Win32_PrintJob`; logs job submitted/completed with document, owner, printer, page count |
| IP-based rough location | `Core/IpLocationMonitor.cs` | City-level only, needs internet access |
| Anti-tamper | `EmployeeAgent.Service/` | A real Windows Service (LocalSystem, `sc failure` auto-restart) that supervises a per-user-session `EmployeeAgent.exe`, relaunching it into any interactive session where it's missing |
| Backend sync | `Core/SyncLoop.cs` | Opt-in (`EMPLOYEEAGENT_BACKEND_URL`); local log stays the write-ahead buffer regardless |
| Report generation | `tools/generate_report.py` | Turns `activity_<machine>.log` into `report.md`, entirely independent of the backend |

## Requirements

- **Agent**: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download).
  Administrator/elevated rights for per-process network usage and file-open
  tracking (ETW) — without elevation those two features log a single
  failure event and are skipped, everything else works normally. Chrome or
  Edge if you want full-URL website tracking.
- **Backend**: Docker + Docker Compose (everything else runs inside
  containers — you don't need Python or PostgreSQL installed locally).
- **Dashboard**: Node.js 20+.
- **Report script**: Python 3.10+ — nothing else needs it, works independent
  of the backend or Docker.

## Setup (agent only)

```powershell
cd EmployeeAgent
dotnet restore
dotnet run
```

`dotnet restore` pulls in `System.Management` (USB/printer WMI) and
`Microsoft.Diagnostics.Tracing.TraceEvent` (ETW-based network/file-open
tracking).

All data is written to:

```
C:\ProgramData\EmployeeAgent\activity_<MACHINENAME>.log   <- all events (JSON-lines)
C:\ProgramData\EmployeeAgent\Screenshots\                  <- periodic screenshots, one file per monitor
```

## Generating the visual report

Once the agent has been running a while (or you've tested a few features
manually), generate the report — this works whether or not the backend is
in the picture, straight from the local log file:

```powershell
cd tools
python generate_report.py
```

This reads the default log path automatically and writes `report.md` in
the current folder. To point at a specific log or output location:

```powershell
python generate_report.py "C:\ProgramData\EmployeeAgent\activity_SOMEPC.log" --out my_report.md
```

The report includes: session summary, idle vs active time with a
breakdown table, top apps by time-in-foreground (idle time excluded),
websites visited (full URL if the browser extension is installed, otherwise
best-effort domain), file activity (including real opens), screenshots
taken, per-process network usage, printer activity, USB/device events,
location estimates, and lock/unlock + anti-tamper events — all as clean
Markdown tables.

## Full URL website tracking (browser extension + native host)

Domain-level tracking works out of the box with no setup, but is best-effort
(see limitations below). For real full-URL tracking:

1. **Publish the native messaging host:**
   ```powershell
   cd EmployeeAgent.NativeHost
   dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
   ```
2. **Load the extension:** open `chrome://extensions` (or
   `edge://extensions`), enable Developer mode, "Load unpacked", select the
   `browser-extension/` folder. Note the Extension ID it's assigned.
3. **Register the native host**, pointing it at that extension ID:
   ```powershell
   cd install
   .\register-native-host.ps1 -ExtensionId "<the ID from step 2>"
   ```
4. Browse a few sites, then check `activity_<machine>.log` for
   `website_visited` events.

For a real rollout (not just local testing), package the extension with a
fixed `key` in `manifest.json` so its ID is stable across installs, and
force-install it machine-wide via Chrome/Edge's `ExtensionInstallForcelist`
Group Policy instead of loading it unpacked per machine.

## Running the anti-tamper Windows Service

Anti-tamper is a real Windows Service (`EmployeeAgent.Service`), not a
separate always-visible watchdog `.exe`. It runs as `LocalSystem`, is
configured with an OS-enforced `sc failure` restart policy, and its only job
is making sure an `EmployeeAgent.exe` is running in every active interactive
session — relaunching one if it's missing.

1. Build/publish both the agent and the service, to the same folder:
   ```powershell
   cd EmployeeAgent
   dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
   cd ..\EmployeeAgent.Service
   dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
   ```
2. Install the service (as Administrator):
   ```powershell
   cd install
   .\install-service.ps1
   ```
3. Test it: kill `EmployeeAgent.exe` via Task Manager — within ~30 seconds
   the service should relaunch it in your session, and you'll see a
   `session_agent_relaunched` event in `activity_<machine>.log`. Kill the
   service itself (`Stop-Service EmployeeAgentService` won't demonstrate this
   — try ending its process in Task Manager) and Windows' Service Control
   Manager restarts the service process itself per the `sc failure` policy.

**Why a separate service instead of just making the agent itself a
service:** a `LocalSystem` Windows Service runs in Session 0, isolated from
any logged-on user's desktop — it cannot take a screenshot or read a
foreground window of what the user sees. So the actual monitoring stays in
a normal per-user-session process (`EmployeeAgent.exe`), and the service's
only job is supervising that process across sessions.

**Honest limitation:** `sc failure` protects against the *process* being
killed or crashing — it does not stop a local administrator from
deliberately running `sc stop EmployeeAgentService` or disabling the service
outright. Restricting who can do that is a Group Policy setting
(`Computer Configuration > Windows Settings > Security Settings > System
Services`) that your IT admins configure in Active Directory — it's
tenant-specific and out of scope for this repo to ship as code.

## Known limitations (be upfront about these)

- **Domain-level website tracking** (the no-setup fallback) is extracted
  from window titles via regex — this only works when the site happens to
  put its own domain in the title. Install the browser extension for
  reliable full-URL tracking instead.
- **Full-URL tracking** only covers Chrome/Edge; Firefox isn't implemented.
- **File `Changed` events can fire multiple times per save** — no
  debouncing is applied, so counts may look inflated for actively edited
  files. **`file_opened`** events fire on any handle open, including
  read-only opens (e.g. just viewing a file) — not only edits.
- **Per-process network usage** requires the agent to run elevated
  (Administrator) — ETW kernel trace sessions are admin-only. Without
  elevation, network usage isn't tracked at all (no whole-machine fallback
  anymore). Loopback/UDP edge cases may still be undercounted even when
  elevated — this is best-effort attribution, not packet-perfect accounting.
- **Printer job tracking** relies on polling `Win32_PrintJob` every 15s and
  diffing job IDs — a very short-lived job could theoretically be missed
  between polls.
- **IP-based location** is city-level at best, and wrong entirely behind
  a VPN — this is not GPS.
- **Anti-tamper** stops the agent/service *processes* from staying down —
  it does not stop an administrator from disabling the service via Group
  Policy restrictions that this repo doesn't configure (see above).
- **Screenshots and the activity log itself contain sensitive data** — the
  local file isn't encrypted at rest, and once events reach the backend
  they're stored in plain PostgreSQL columns/JSONB, not field-level
  encrypted. Both need the same encryption-at-rest and access-control
  treatment as any other system handling employee monitoring data before a
  real (non-pilot) rollout.
- **Dashboard export/report generation is unauthenticated by network
  position only** — anyone who can reach the API and has valid credentials
  for a role can export data their role can see; there's no additional
  step-up auth (e.g. re-entering a password) before a bulk export, only the
  audit-log trail after the fact.
- **PDF export was explicitly skipped** (CSV/XLSX only, per the original
  scope) and the WebSocket alert broadcast (`/api/v1/ws/alerts`) only works
  correctly with a single API replica — see `backend/README.md`'s
  architecture notes for both.

## Running a multi-laptop pilot (no backend needed)

For a PM demo across 3 laptops, you don't need the backend running — you can
still just collect the 3 laptops' log files into one folder and generate one
combined report. (If the backend *is* running and all 3 agents have
`EMPLOYEEAGENT_BACKEND_URL` set, the dashboard gives you the same overview
live instead — this manual method is for when you want a quick demo without
standing up infrastructure.)

**Step 1 — get logs writing with a device-identifiable filename** (already
done: every agent writes to `activity_<MACHINENAME>.log`, not a generic
`activity.log`, so files from different laptops never collide).

**Step 2 — pick a collection method:**

- **Manual (simplest, recommended for 3 devices):** after each test
  session, copy `activity_<MACHINENAME>.log` off each laptop via USB or
  email, and drop all 3 into one folder on your machine.
- **Shared folder (semi-automatic):** on each laptop, before running the
  agent, set an environment variable pointing at a shared OneDrive/Google
  Drive/network folder:
  ```powershell
  setx EMPLOYEEAGENT_LOG_DIR "C:\Users\<user>\OneDrive\EmployeeAgentPilot"
  ```
  All 3 agents will then write directly into that shared folder — no
  manual copying needed, and (if it's a cloud-synced folder) you'll see
  new events appear on your machine automatically. Every component (the
  Windows Service, native messaging host, and SyncLoop) honors the same
  environment variable, so all of them stay pointed at the same log file
  per machine.

**Step 3 — generate the combined report:**

```powershell
cd tools
python generate_report.py --dir "C:\path\to\the\folder\with\3\log\files" --out pilot_report.md
```

This produces one Markdown file starting with an **overview table**
comparing all 3 devices (event count, log span, idle/active %, top app),
followed by a full detailed breakdown per device.

*(If you only ever collect one laptop's log, `generate_report.py` still
works exactly as before — no `--dir` needed, just point it at the file.)*

## What's next

The core loop (agent → backend → dashboard) is complete and working
end-to-end. Reasonable next steps, roughly in priority order:

- **Production security hardening**: encrypt the local log and screenshots
  at rest, add field-level encryption for sensitive `activity_events`
  columns in Postgres, and configure the Group Policy restriction on who
  can stop `EmployeeAgentService` (see above).
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
