# Employee Monitoring Agent

The Windows-side of this stack: a per-user-session agent that captures
activity locally, an anti-tamper Windows Service that supervises it, and a
Native Messaging host + browser extension for full-URL tracking. Every
event is queued locally in SQLite just long enough to survive a network
blip, then streamed to the backend in small, frequent batches — that's what
the frontend dashboard reads from. See [DEVELOPMENT.md](DEVELOPMENT.md) to
wire the backend connection up.

```
monitoring-agent/
  EmployeeAgent/            per-user-session Windows agent (WinForms, no visible window)
  EmployeeAgent.Service/    Windows Service - OS-enforced anti-tamper supervisor
  EmployeeAgent.NativeHost/ Chrome/Edge native messaging host - full URL tracking
  browser-extension/        Manifest V3 extension - reports tab URLs to the native host
  install/                  PowerShell scripts (Windows Service + native host registration)
```

See [monitoring-agent/CODE_EXPLAINED.md](monitoring-agent/CODE_EXPLAINED.md)
for a file-by-file walkthrough of everything in that folder.

## What's implemented

| Feature | Where | Notes |
|---|---|---|
| Login/Logout | `Core/AgentContext.cs` | `logout` (and lock/unlock, remote connect/disconnect) come from `SystemEvents.SessionSwitch` live; `login` is backdated at startup from the OS's own session logon time (`Core/SessionInfo.cs`) instead, since the agent starts after the real logon already happened and would otherwise never see it via that event |
| Idle/Active detection | `Core/IdleTimeMonitor.cs` | 5-minute threshold (configurable) |
| Active window & app tracking | `Core/WindowActivityMonitor.cs` | Polls every 3s, logs only on change |
| App usage duration | backend `aggregation.py` (`compute_app_usage`) | Derived from consecutive focus-change timestamps, with idle time subtracted out so time-away-with-an-app-focused isn't counted as usage |
| Website tracking (domain-level) | `Core/WindowActivityMonitor.cs` | Best-effort regex fallback on browser window titles — used only when the browser extension isn't installed |
| Website tracking (full URL) | `browser-extension/` + `EmployeeAgent.NativeHost/` | Manifest V3 extension reports every tab navigation to a Native Messaging host, which writes `website_visited` events straight to the shared local queue. Chrome/Edge only — Firefox not implemented |
| File activity | `Core/FileActivityMonitor.cs` | `FileSystemWatcher` for create/rename/change/delete + ETW (`Microsoft-Windows-Kernel-File`) for real **open** events, all scoped to Desktop, Documents, Downloads |
| Screenshot capture | `Core/ScreenshotCapture.cs` | Every 10 minutes by default, one PNG per connected monitor |
| Network/bandwidth usage | `Core/NetworkUsageMonitor.cs` | Per-process, via ETW (`Microsoft-Windows-Kernel-Network`) — not just whole-machine totals |
| USB/device activity | `Core/DeviceActivityMonitor.cs` | Via WMI `Win32_DeviceChangeEvent` |
| Printer usage | `Core/PrinterActivityMonitor.cs` | Polls WMI `Win32_PrintJob`; logs job submitted/completed with document, owner, printer, page count |
| IP-based rough location | `Core/IpLocationMonitor.cs` | City-level only, needs internet access |
| Anti-tamper | `EmployeeAgent.Service/` | A real Windows Service (LocalSystem, `sc failure` auto-restart) that supervises a per-user-session `EmployeeAgent.exe`, relaunching it into any interactive session where it's missing |
| Backend sync | `Core/SyncLoop.cs` | Opt-in (`EMPLOYEEAGENT_BACKEND_URL`); ticks every 4s and sends whatever's queued in small batches — see [DEVELOPMENT.md](DEVELOPMENT.md) |

## Requirements

- Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download).
- Administrator/elevated rights for per-process network usage and
  file-open tracking (ETW) — without elevation those two features log a
  single failure event and are skipped, everything else works normally.
- Chrome or Edge if you want full-URL website tracking.

## Setup (agent only)

```powershell
cd monitoring-agent\EmployeeAgent
dotnet restore
dotnet run
```

`dotnet restore` pulls in `System.Management` (USB/printer WMI) and
`Microsoft.Diagnostics.Tracing.TraceEvent` (ETW-based network/file-open
tracking).

Locally, the agent only ever keeps:

```
C:\ProgramData\EmployeeAgent\events_<MACHINENAME>.db       <- local SQLite queue: events pending sync to the backend
C:\ProgramData\EmployeeAgent\Screenshots\                  <- periodic screenshots, one file per monitor
```

There is no persistent activity log - `events_<MACHINENAME>.db` is a
retry-on-reconnect buffer, not a report: every row is deleted the moment
the backend confirms it accepted it (see "Backend sync" above). With no
`EMPLOYEEAGENT_BACKEND_URL` configured, events simply accumulate in that
queue unsent instead of being lost, and there's currently no separate
cleanup for that case.

The queue directory can be overridden with the `EMPLOYEEAGENT_LOG_DIR`
environment variable (see "multi-laptop pilot" below) — all writers
(agent, service, native host) honor it identically.

## Full URL website tracking (browser extension + native host)

Domain-level tracking works out of the box with no setup, but is best-effort
(see limitations below). For real full-URL tracking:

1. **Publish the native messaging host:**
   ```powershell
   cd monitoring-agent\EmployeeAgent.NativeHost
   dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
   ```
2. **Load the extension:** open `chrome://extensions` (or
   `edge://extensions`), enable Developer mode, "Load unpacked", select the
   `monitoring-agent/browser-extension/` folder. Note the Extension ID it's
   assigned.
3. **Register the native host**, pointing it at that extension ID:
   ```powershell
   cd monitoring-agent\install
   .\register-native-host.ps1 -ExtensionId "<the ID from step 2>"
   ```
4. Browse a few sites, then check for `website_visited` events: in the
   dashboard's device event timeline if `EMPLOYEEAGENT_BACKEND_URL` is
   configured (events sync within a few seconds), or by opening
   `events_<machine>.db` with any SQLite browser if you're testing without a
   backend - rows stay queued there until synced.

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
   cd monitoring-agent\EmployeeAgent
   dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
   cd ..\EmployeeAgent.Service
   dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
   ```
2. Install the service (as Administrator):
   ```powershell
   cd monitoring-agent\install
   .\install-service.ps1
   ```
3. Test it: kill `EmployeeAgent.exe` via Task Manager — within ~30 seconds
   the service should relaunch it in your session, and you'll see a
   `session_agent_relaunched` event show up (dashboard, or
   `events_<machine>.db` if no backend is configured - see "Full URL
   website tracking" above for how to check either). Kill the service
   itself (`Stop-Service EmployeeAgentService` won't demonstrate this — try
   ending its process in Task Manager) and Windows' Service Control Manager
   restarts the service process itself per the `sc failure` policy.

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
- **Screenshots and the local event queue contain sensitive data** — the
  SQLite queue file and screenshots aren't encrypted at rest (the queue is
  usually short-lived, draining within seconds of a synced batch, but still
  holds plaintext while pending), and once events reach the backend they're
  stored in plain PostgreSQL columns/JSONB, not field-level encrypted. All
  of it needs the same encryption-at-rest and access-control treatment as
  any other system handling employee monitoring data before a real
  (non-pilot) rollout.

## Packaging a distributable installer (MSI) for company-wide rollout

The publish/copy/script steps above are fine for testing on a handful of
machines, but don't scale to "IT rolls this out to every employee laptop."
For that, `monitoring-agent/install/EmployeeAgent.Installer/` builds a single
`EmployeeAgent.msi` that installs the agent + native host, and installs
**and starts** `EmployeeAgentService` with the same restart-on-failure
policy `install-service.ps1` configures by hand - so there's one artifact
to hand to IT instead of a zip and a checklist.

Requires the .NET 8 SDK and the WiX v4 CLI on a **Windows** build machine
(these projects don't build on Linux/macOS):

```powershell
dotnet tool install --global wix
wix extension add WixToolset.Util.wixext

cd monitoring-agent\install
.\build-installer.ps1 -BackendUrl "https://backend.your-company.com" -Version "1.0.0"
```

This bakes the given backend URL in as every install's default
`EMPLOYEEAGENT_BACKEND_URL` (as a machine-wide environment variable + an
`HKLM\Software\EmployeeAgent` registry value) so employees/IT don't need to
run `setx` by hand per machine. It can still be overridden per-install with
`msiexec /i EmployeeAgent.msi BACKENDURL=...`, or left blank at build time
(`-BackendUrl ""`) if you'd rather push that value some other way (registry
GPO Preference, etc.).

The MSI does **not** bundle the .NET 8 Desktop Runtime (target machines need
it pre-installed, same assumption the manual `dotnet publish` steps make)
and does not handle the browser extension / native-messaging registration -
that still needs a fixed extension ID + `ExtensionInstallForcelist` Group
Policy per the "Full URL website tracking" section above, since the
extension ID isn't known at MSI build time.

**Rolling it out fleet-wide**, once you have `EmployeeAgent.msi`:

- **Active Directory domain**: Group Policy Management Console → new GPO →
  Computer Configuration → Policies → Software Settings → Software
  Installation → New → Package → point at the MSI on a share reachable by
  every target machine → Assigned. It installs silently at next boot for
  every computer the GPO applies to - no per-employee action needed.
- **Intune / Azure AD-managed devices**: Apps → Windows → Add → Win32 app
  (`.intunewin`-wrap the MSI with the Win32 Content Prep Tool), install
  command `msiexec /i EmployeeAgent.msi /quiet BACKENDURL=...`, assign to a
  device group as Required.
- **Anything else (RMM tool, PSExec, no central management yet)**: run
  `msiexec /i EmployeeAgent.msi /quiet` remotely through whatever remote-exec
  mechanism IT already has.

Bump `-Version` and re-run `build-installer.ps1` for updates - `MajorUpgrade`
in the MSI handles upgrading an existing install in place, so redeploying
the new MSI through the same GPO/Intune assignment is enough; there's no
separate uninstall-then-reinstall step to script.

## Running a multi-laptop pilot

For a PM demo across multiple laptops, point every agent's
`EMPLOYEEAGENT_BACKEND_URL` at the same backend instance:

```powershell
setx EMPLOYEEAGENT_BACKEND_URL "http://<your-backend-host>:8000"
```

Every agent queues into its own `events_<MACHINENAME>.db` locally (so
devices never collide even offline) and streams it to the shared backend
via `SyncLoop` — see [DEVELOPMENT.md](DEVELOPMENT.md). The dashboard then
shows a live overview across all enrolled devices with no manual log
collection or report generation needed.
