# Employee Agent — Full Feature Build (No Backend Yet)

This build implements every feature from the original plan, storing
everything to a **local activity log file** (no backend/database exists
yet — that's the next stage). It includes OS-enforced anti-tamper via a real
Windows Service, full-URL browser tracking via a native-messaging browser
extension, per-process network attribution via ETW, real file-open tracking
via ETW, printer job tracking via WMI, multi-monitor screenshots, and a
script that turns the raw log into a readable Markdown report.

## What's implemented

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
| Report generation | `tools/generate_report.py` | Turns `activity_<machine>.log` into `report.md` |

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Python 3.10+ (only for the report script — nothing else needs it)
- **Administrator/elevated rights** for the agent process — starting the ETW
  kernel trace sessions used for per-process network usage and file-open
  tracking is an admin-only operation. Without elevation, those two features
  log a single failure event and are simply skipped (everything else in the
  agent still works normally).
- Chrome or Edge (Chromium-based) if you want full-URL website tracking —
  Firefox isn't supported by this build.

## Setup

```powershell
cd EmployeeAgent
dotnet restore
dotnet run
```

Run an elevated (Administrator) PowerShell/terminal if you want per-process
network usage and file-open tracking to work — otherwise those two features
silently no-op (see Requirements above).

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
manually), generate the report:

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

Anti-tamper is now a real Windows Service (`EmployeeAgent.Service`), not a
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
- **Screenshots and the activity log itself contain sensitive data** —
  once a backend exists, both need the same encryption-at-rest and
  access-control treatment described in the original architecture plan.

## Running a multi-laptop pilot (no backend yet)

For a PM demo across 3 laptops, you don't need a backend — you need the 3
laptops' log files in one folder, then one combined report.

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
  new events appear on your machine automatically. The Windows Service and
  native messaging host both honor the same environment variable, so all
  three components stay pointed at the same log file per machine.

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

## Next step (once you've validated this locally)

Replace `ActivityLogger.Log()`'s file-write with an HTTPS POST to your
backend API (once it exists), and switch local storage to the encrypted
SQLite buffer from the original architecture — so events survive network
outages instead of just accumulating forever in a flat file. The Windows
Service, native messaging host, and browser extension would need the same
treatment (or route everything through the local agent instead of writing
to the log file directly) once that backend exists.
