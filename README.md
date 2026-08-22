# Employee Agent — Full Feature MVP (No Backend Yet)

This build implements every "Can Build" feature from the plan, storing
everything to a **local activity log file** (no backend/database exists
yet — that's the next stage). It also includes a basic anti-tamper
watchdog and a script that turns the raw log into a readable Markdown
report.

## What's implemented

| Feature | Where | Notes |
|---|---|---|
| Login/Logout | `Core/AgentContext.cs` (`SystemEvents.SessionSwitch`) | Also captures lock/unlock, remote connect/disconnect |
| Idle/Active detection | `Core/IdleTimeMonitor.cs` | 5-minute threshold (configurable) |
| Active window & app tracking | `Core/WindowActivityMonitor.cs` | Polls every 3s, logs only on change |
| App usage duration | *(derived, not stored directly)* | Computed by `generate_report.py` from consecutive focus-change timestamps |
| Website tracking (domain-level) | `Core/WindowActivityMonitor.cs` | Best-effort regex on browser window titles — see limitation below |
| Website tracking (full URL) | ❌ Not included | Requires a separate browser extension component |
| File activity | `Core/FileActivityMonitor.cs` | Watches Desktop, Documents, Downloads |
| Screenshot capture | `Core/ScreenshotCapture.cs` | Every 10 minutes by default |
| Network/bandwidth usage | `Core/NetworkUsageMonitor.cs` | Whole-machine, not per-app |
| USB/device activity | `Core/DeviceActivityMonitor.cs` | Via WMI `Win32_DeviceChangeEvent` |
| IP-based rough location | `Core/IpLocationMonitor.cs` | City-level only, needs internet access |
| Anti-tamper (basic) | `EmployeeAgent.Watchdog/Program.cs` | Separate process, restarts the agent if killed |
| Report generation | `tools/generate_report.py` | Turns `activity.log` into `report.md` |

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Python 3.10+ (only for the report script — nothing else needs it)

## Setup

```powershell
cd EmployeeAgent
dotnet restore
dotnet run
```

`dotnet restore` will pull in the one new dependency (`System.Management`,
needed for the USB/device monitor).

All data is written to:

```
C:\ProgramData\EmployeeAgent\activity.log       <- all events (JSON-lines)
C:\ProgramData\EmployeeAgent\Screenshots\        <- periodic screenshots
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
python generate_report.py "C:\ProgramData\EmployeeAgent\activity.log" --out my_report.md
```

The report includes: session summary, idle vs active time with a
breakdown table, top apps by time-in-foreground, best-effort websites
visited, file activity, screenshots taken, network usage totals,
USB/device events, location estimates, and lock/unlock + anti-tamper
events — all as clean Markdown tables. **This script has been tested
against sample data and confirmed working correctly** — you can trust it
to run against your real log as-is.

## Running the anti-tamper watchdog

The watchdog is a **separate executable** from the agent, on purpose — if
they were the same process, killing it once would defeat both.

1. Build/publish the agent first and note the exe path, e.g.:
   ```powershell
   cd EmployeeAgent
   dotnet publish -c Release -o "C:\Program Files\EmployeeAgent"
   ```
2. Open `EmployeeAgent.Watchdog/Program.cs` and confirm `AgentExePath`
   matches where you just published to.
3. Run the watchdog (ideally from a separate terminal, or eventually as
   its own scheduled task):
   ```powershell
   cd EmployeeAgent.Watchdog
   dotnet run
   ```
4. Test it: kill `EmployeeAgent.exe` via Task Manager — within 30 seconds
   the watchdog should relaunch it, and you'll see an
   `agent_restarted_by_watchdog` event in `activity.log`.

**Honest limitation:** this is the *basic* tier of anti-tamper, as scoped.
A user can still kill the watchdog itself, or kill both processes in quick
succession. Real production-grade tamper resistance means converting the
agent into an actual **Windows Service** with OS-level recovery policies
(`sc failure ... reset= 0 actions= restart/5000`), running under the
SYSTEM account, and using Group Policy to restrict who can stop the
service. That conversion is a good next milestone once this feature set
is validated.

## Known limitations (be upfront about these)

- **Website domains** are extracted from window titles via regex — this
  only works when the site happens to put its own domain in the title.
  Most modern sites (Gmail, Notion, etc.) show a page title instead.
  Treat this as a loose signal, not a browsing history.
- **File `Changed` events can fire multiple times per save** — no
  debouncing is applied yet, so counts may look inflated for actively
  edited files.
- **Network usage is machine-wide**, not attributable to a specific app.
- **IP-based location** is city-level at best, and wrong entirely behind
  a VPN — this is not GPS.
- **Screenshots and the activity log itself contain sensitive data** —
  once a backend exists, both need the same encryption-at-rest and
  access-control treatment described in the original architecture plan.

## Running a multi-laptop pilot (no backend yet)

For a PM demo across 3 laptops, you don't need a backend — you need the 3
laptops' log files in one folder, then one combined report.

**Step 1 — get logs writing with a device-identifiable filename** (already
done: every agent now writes to `activity_<MACHINENAME>.log`, not a
generic `activity.log`, so files from different laptops never collide).

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
  new events appear on your machine automatically.

**Step 3 — generate the combined report:**

```powershell
cd tools
python generate_report.py --dir "C:\path\to\the\folder\with\3\log\files" --out pilot_report.md
```

This produces one Markdown file starting with an **overview table**
comparing all 3 devices (event count, log span, idle/active %, top app),
followed by a full detailed breakdown per device — exactly what you'd
show your PM. This has been tested end-to-end with 3 simulated device
logs and confirmed working correctly.

*(If you only ever collect one laptop's log, `generate_report.py` still
works exactly as before — no `--dir` needed, just point it at the file.)*

## Next step (once you've validated this locally)

Replace `ActivityLogger.Log()`'s file-write with an HTTPS POST to your
backend API (once it exists), and switch local storage to the encrypted
SQLite buffer from the original architecture — so events survive network
outages instead of just accumulating forever in a flat file.
