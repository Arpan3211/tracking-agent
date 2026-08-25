# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Employee Agent — a Windows monitoring agent that syncs to a backend and
dashboard. It captures activity events (login/logout, idle/active, active
window, full/domain website visits, file create/rename/change/delete/open,
screenshots, per-process network usage, USB/device events, printer jobs,
IP-based location), queues them locally in SQLite only long enough to
survive a network blip, and streams them to a FastAPI backend in small,
frequent batches for a React dashboard to display. A Windows Service
supervises the per-session agent process for OS-enforced anti-tamper. A
browser extension + native messaging host report full URLs.

There are three .NET projects (no `.sln` file — build/run each project
directory directly) and one browser extension:

All monitoring-agent code lives under `monitoring-agent/`:

- `monitoring-agent/EmployeeAgent/` — the agent itself (WinForms app, no
  visible window; runs per logged-in user session)
- `monitoring-agent/EmployeeAgent.Service/` — Windows Service (`LocalSystem`)
  that supervises `EmployeeAgent.exe` across sessions; the anti-tamper layer
- `monitoring-agent/EmployeeAgent.NativeHost/` — Chrome/Edge Native Messaging
  host; receives full URLs from the browser extension and logs them
- `monitoring-agent/browser-extension/` — Manifest V3 extension
  (`background.js`), no build step, loaded unpacked or force-installed via
  Group Policy

The backend and frontend live in their own top-level folders:

- `backend/` — FastAPI + PostgreSQL API (ingestion, dashboard, alerts)
- `frontend/` — React + TypeScript dashboard (Vite)

## Requirements

- Windows 10/11 (all three .NET projects target `net8.0`/`net8.0-windows`
  and use WinForms/WMI/Win32/ETW/WTS APIs — this code will not build or run
  on Linux/macOS)
- .NET 8 SDK
- Administrator/elevated rights for `EmployeeAgent.exe` — ETW kernel trace
  sessions (per-process network usage, file-open tracking) are admin-only;
  without elevation those two features no-op with a single failure event
  logged, everything else still works

## Common commands

Run from within each project directory (there's no top-level solution file):

```powershell
# Agent (per-user-session process)
cd monitoring-agent\EmployeeAgent
dotnet restore
dotnet run
dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"

# Windows Service (anti-tamper supervisor, run after publishing the agent)
cd monitoring-agent\EmployeeAgent.Service
dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
cd ..\install
.\install-service.ps1        # must run elevated

# Native messaging host (full-URL browser tracking)
cd monitoring-agent\EmployeeAgent.NativeHost
dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
cd ..\install
.\register-native-host.ps1 -ExtensionId "<id from chrome://extensions>"
```

```powershell
# Backend (FastAPI + PostgreSQL, via docker-compose.yml at repo root)
docker compose up -d db
docker compose up -d api

# Frontend dashboard (React + Vite)
cd frontend
npm install
npm run dev
```

There is no test suite for the three .NET projects or the browser extension.
The backend has pytest coverage (`cd backend && pytest`) for
ingestion/auth/RBAC — see [backend/README.md](backend/README.md).

## Architecture

**`AgentContext` (`monitoring-agent/EmployeeAgent/Core/AgentContext.cs`) is the composition
root for the per-session agent** — it owns every monitor instance and their
polling timers, and is the only class aware of the full in-process feature
set. Each monitor class is independent and only knows how to do its own job
(poll/log); they don't know about each other. When adding a new monitor,
wire it up here the same way the existing ones are (construct it, optionally
call `.Start()`, add a `Timer` via `StartTimer(...)` if it's poll-based).

**Four independent processes all write into the same local SQLite event
queue** — the per-session `EmployeeAgent.exe`, `EmployeeAgent.Service.exe`,
`EmployeeAgent.NativeHost.exe`, and (indirectly, via the native host) the
browser extension. They deliberately don't share an assembly: each
duplicates a small "resolve queue path + insert one row" helper by hand
(`ActivityLogger` in the main agent; `ActivityLog` in the Service; the
inline write in the NativeHost) rather than depending on a shared library,
so killing or crashing any one process can't take another down; SQLite's
WAL mode plus a busy-timeout on every connection is what lets three
processes insert into the same file safely. **The event shape is the
contract all of them must stay in sync on**: `EventType`, `TimestampUtc`,
and `Details` — a real `Dictionary<string, string>` built per event type by
whichever monitor raised it (e.g. `{"process": "chrome", "title": "..."}`
for `app_focus_change`), sent to the backend and stored as JSONB exactly as
given, with no parsing step on either end. Only `EmployeeAgent.exe`'s
`SyncLoop` (see below) ever reads from or deletes out of the queue - the
other three processes only ever insert. The queue directory defaults to
`%ProgramData%\EmployeeAgent` but can be overridden via the
`EMPLOYEEAGENT_LOG_DIR` env var — all four writers honor it identically,
which is what makes the multi-laptop pilot mode work (see README).

**Anti-tamper is a two-layer supervision chain, not a single process**:
Windows' Service Control Manager restarts `EmployeeAgent.Service` itself if
it's killed (OS-enforced, via the `sc failure` policy `install-service.ps1`
configures); `EmployeeAgent.Service`'s `SessionSupervisor` in turn watches
every active interactive session and relaunches `EmployeeAgent.exe` into any
session where it's missing. The service **cannot** do the agent's actual
monitoring job itself — a `LocalSystem` service runs in Session 0, isolated
from any user's desktop, so it structurally cannot capture a screenshot or
read a foreground window. That constraint is *why* the split exists.
Cross-session process launch uses the standard SYSTEM→user-session P/Invoke
pattern in `monitoring-agent/EmployeeAgent.Service/SessionInterop.cs`
(`WTSQueryUserToken` → `DuplicateTokenEx` → `CreateEnvironmentBlock` →
`CreateProcessAsUser`) — if you need to touch that file, don't simplify away
any of those four calls; each does a distinct part of impersonating the
target user's session that `CreateProcess` alone can't do.

**Full-URL tracking is fire-and-forget, not request/response with the
agent**: the browser extension calls `chrome.runtime.sendNativeMessage`
per navigation, which spawns `EmployeeAgent.NativeHost.exe` fresh for that
one message (see `Program.cs`'s single read-message/write-response/exit
flow — it is not a long-running process and does not loop). The host writes
directly to the shared local queue itself; it has no dependency on whether
`EmployeeAgent.exe` is even running. `WindowActivityMonitor`'s regex-based
domain extraction is unchanged and still runs unconditionally as a fallback
signal for machines without the extension installed — the backend prefers
`website_visited` (full URL) events over the domain fallback when both are
present for a device.

**Per-process network usage and file-open tracking both use ETW** (Event
Tracing for Windows, via the `Microsoft.Diagnostics.Tracing.TraceEvent`
NuGet package) rather than polling — `NetworkUsageMonitor` subscribes to the
`Microsoft-Windows-Kernel-Network` provider's TCP/UDP send/recv events
(which carry the owning process ID) and batches counts, flushed to the
queue on the existing 30s timer via `Flush()`; `FileActivityMonitor` subscribes to
`Microsoft-Windows-Kernel-File`'s `FileIOCreate` events (filtered to the
three watched folders) for real open detection, alongside the pre-existing
`FileSystemWatcher`-based create/rename/change/delete events. Both require
the process to run elevated — see Requirements.

**All reporting now happens in the backend and frontend, not the agent** —
there is no local report-generation tool; the agent's only job is capturing
events and syncing the raw log to the backend via `SyncLoop`. The backend's
`aggregation.py` (`compute_app_usage()`) takes `idle_periods` and subtracts
idle-overlap from each app-focus interval, so leaving an app focused while
away from the machine doesn't inflate its usage time — any future change to
app-duration logic should preserve that idle-subtraction, not just revert to
raw interval length. See [backend/README.md](backend/README.md) for how the
dashboard's session summary, app usage, and other views are built from the
synced events.

## Known limitations worth knowing before changing related code

- Domain-level website tracking (`WindowActivityMonitor`'s regex fallback)
  only works when the browser happens to put the URL in the window title —
  a loose signal, used only when the extension/native host isn't present.
- Full-URL tracking only covers Chrome/Edge (Chromium native messaging);
  Firefox is not implemented.
- `FileActivityMonitor`'s `FileSystemWatcher` `Changed` event isn't
  debounced — a single save can fire multiple `file_changed` events. The ETW
  `file_opened` event also fires on read-only opens, not just edits.
- Per-process network attribution via ETW is best-effort — loopback/UDP edge
  cases may be undercounted, and it requires elevation (no whole-machine
  fallback exists anymore if unelevated; the feature is simply skipped).
- `PrinterActivityMonitor` polls `Win32_PrintJob` every 15s and diffs job
  IDs rather than using a WQL event subscription (print-job event queries
  are known to be flaky across driver/spooler combinations) — a very
  short-lived job could theoretically fall between two polls.
- `IpLocationMonitor` gives city-level location from a free public API
  (ip-api.com) and is wrong under a VPN/proxy — not GPS.
- Anti-tamper stops the *processes* from staying down (service restart +
  cross-session relaunch); it does not stop an administrator from
  deliberately disabling the service. Restricting that requires a Group
  Policy Object configured by IT admins in Active Directory — intentionally
  not something this repo ships as code (see README's Windows Service
  section for the exact policy path to configure).

## Planned next step (not yet implemented)

The agent already syncs to the backend over HTTPS (`SyncLoop`, opt-in via
`EMPLOYEEAGENT_BACKEND_URL`) via a local SQLite queue that drains itself the
moment events are confirmed synced, rather than an ever-growing flat file -
the queue is not encrypted at rest, though, and neither are the local
`device_credentials_*.json`/screenshot files sitting alongside it.
Encrypting all of that local state is the next step before a real
(non-pilot) rollout.
