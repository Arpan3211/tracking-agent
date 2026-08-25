# Monitoring Agent — Code Explained

A file-by-file walkthrough of everything under `monitoring-agent/`. This
covers what each file does and why it's built the way it is — not a
line-by-line commentary. See [../CLAUDE.md](../CLAUDE.md) for the
cross-cutting architecture notes and [AGENT.md](../AGENT.md) for setup/run
instructions.

There are four independent pieces here, each its own OS process, all
writing into the same local SQLite event queue: `EmployeeAgent` (the actual
monitor), `EmployeeAgent.Service` (the anti-tamper supervisor),
`EmployeeAgent.NativeHost` (the browser bridge), and `browser-extension`
(runs inside Chrome/Edge, not a .NET project at all). There is no local
flat-file log or standalone report anymore - every monitored event is
queued locally only long enough to be sent to the backend; see
`Core/ActivityLogger.cs` and `Core/SyncLoop.cs` below for how that queue
and its drain loop work.

## `EmployeeAgent/` — the per-user-session agent

**`Program.cs`** — the entry point. It's a WinForms app (`[STAThread]`,
`Application.Run`) but deliberately shows no window: it hands control
straight to `AgentContext`, which is an `ApplicationContext` rather than a
`Form`. The only reason this is a WinForms app at all (instead of a plain
console app) is that `ApplicationContext` gives it the Win32 message loop
several of the monitors below need (session-switch notifications, the
hidden-form trick).

**`Core/AgentContext.cs`** — the composition root. This is the one class
that knows about every feature that exists; every monitor below is
constructed here, started here, and torn down here in `ExitThreadCore()`.
It does four things: (1) creates an invisible, zero-opacity `Form` purely
because `SystemEvents.SessionSwitch` needs a running message loop to
deliver login/logout/lock/unlock notifications — there's no way to receive
those without one; (2) constructs every monitor and calls `.Start()` on the
two that need it (`DeviceActivityMonitor`, `NetworkUsageMonitor` — both set
up an event subscription rather than being purely poll-driven); (3) starts
one `System.Windows.Forms.Timer` per poll-based monitor, each with its own
interval (window polling every 3s, idle-checking every 5s, network flush
every 30s, screenshots every 10 minutes, and so on — all defined as
constants at the top of the file); (4) wires `SessionSwitch` itself to log
logout/lock/unlock/remote-connect/remote-disconnect events directly, since
that's cheap enough not to need its own monitor class. It does **not**
wire up `login` there, though - `SystemEvents.SessionSwitch` only ever
delivers *future* transitions after a process subscribes to it, and this
process is launched (by the anti-tamper service) up to ~30s *after* the
real Windows logon already happened, so it can never see its own session's
`SessionLogon` that way. Instead, right after constructing `_logger`, the
constructor calls `SessionInfo.GetSessionLogonTimeUtc()` and logs `login`
backdated to that real timestamp - this is what makes session start times
(and therefore session duration, shown in the dashboard's "Session
history") accurate instead of silently missing for the ordinary case of
"agent starts a little after the user logs in."

**`Core/ActivityEvent.cs`** — the one shared data shape everything in this
project logs: a `record` with `EventType`, `TimestampUtc`, and an optional
`Details` — a real `Dictionary<string, string>`, not a delimited string.
Each monitor builds whatever key/value pairs make sense for its own event
type (e.g. `{"process": "chrome", "title": "..."}` for `app_focus_change`)
and that's sent to the backend and stored as JSONB exactly as given - no
parsing step on either end anymore. This shape is the contract every other
process (`EmployeeAgent.Service`, `EmployeeAgent.NativeHost`) has to
independently agree on, since none of them share this assembly.

**`Core/ActivityLogger.cs`** — where every event actually gets queued: a
local SQLite database (`events_<MACHINENAME>.db` under
`%ProgramData%\EmployeeAgent`, or `EMPLOYEEAGENT_LOG_DIR` if set) with a
single `pending_events` table. This is the durability buffer the whole sync
story depends on - not a permanent log, purely a retry-on-reconnect queue:
rows go in via `Log()`, and `SyncLoop` (below) deletes them the moment
they're confirmed synced, so the table only ever holds what's genuinely
still unsent. `Log()` itself never touches disk on the calling thread - it
does an in-memory `Channel<ActivityEvent>` write and returns immediately;
a single background task (`ProcessWritesAsync`, started once in the
constructor) drains that channel to SQLite over one persistent connection.
This matters because `Log()` is called from every monitor, including from
WinForms `Timer` ticks on the agent's one message-loop thread, and the
agent runs unattended 24/7 - it must never be possible for a slow disk
(antivirus scanning the file, momentary contention) to stall a timer or a
monitor's event callback. `ReadPendingAsync()`/`DeleteSyncedUpToAsync()`/
`CountPendingAsync()` (used by `SyncLoop`) each wrap their SQLite work in
`Task.Run` for the same reason, so even the infrequent read/delete side
never blocks whichever thread called it. The connection sets `PRAGMA
journal_mode=WAL` and a busy-timeout, since `EmployeeAgent.Service` and
`EmployeeAgent.NativeHost` insert into this same file from separate
processes and need to retry-on-lock rather than fail outright.

**`Core/IdleTimeMonitor.cs`** — a thin, static wrapper around the Win32
`GetLastInputInfo` API. This is the standard OS-level way to measure
"how long since the last keyboard/mouse input," and it works regardless of
which window has focus — no polyfill or app-level tracking could give the
same guarantee.

**`Core/SessionInfo.cs`** — resolves the real Windows logon time for the
current session via `WTSQuerySessionInformation`, used once by
`AgentContext` at startup to backdate the `login` event (see above). Worth
reading closely if you ever touch it: the obvious-looking `WTSLogonTime`
info class (18) returns `ERROR_NOT_SUPPORTED` for an ordinary local
console session (confirmed by hand against a real session on this
machine) - it only works reliably over RDP. The fix is querying the full
`WTSSessionInfo` struct (24) and reading its `LogonTime` field instead.
That struct's own marshaling is a second trap: without
`CharSet.Unicode` explicitly on the `DllImport`, Windows silently hands
back the narrower ANSI `WTSINFOA` shape instead (144 bytes vs the Unicode
struct's 216), and reading that through a Unicode-shaped struct definition
misaligns every field after the fixed-size name strings - `LogonTime`
came out as a bogus 17th-century date in testing, with no error raised
anywhere. The fix checks `bytesReturned` against the struct's expected
size before trusting anything in it, specifically because that failure
mode is silent, not loud. The final value was verified against
`Win32_LogonSession.StartTime` (WMI) for the same real session before
shipping - matched to the second.

**`Core/WindowActivityMonitor.cs`** — polled every 3 seconds by
`AgentContext`; logs an `app_focus_change` event only when the foreground
window's process or title actually changes (not on every poll). For known
browser processes, it also tries to regex a domain out of the window title
— this is explicitly best-effort, since modern browsers often show the
page title instead of the URL. Reliable full-URL tracking is the separate
browser-extension + native-host pair described below, not this class.

**`Core/FileActivityMonitor.cs`** — combines two independent detection
mechanisms for Desktop/Documents/Downloads: a `FileSystemWatcher` per
folder for create/rename/change/delete (works unprivileged, but its
`Changed` event isn't debounced — a single save can fire multiple times),
and an ETW kernel trace session on `Microsoft-Windows-Kernel-File` for real
file-*open* detection, which `FileSystemWatcher` structurally cannot see.
The ETW half requires elevation; if the process isn't running as
Administrator, `StartFileOpenTracing()` logs one `file_open_tracking_failed`
event and the FileSystemWatcher-based events keep working regardless.

**`Core/ScreenshotCapture.cs`** — captures every connected monitor
(`Screen.AllScreens`) on a 10-minute timer and saves each as its own PNG
under `%ProgramData%\EmployeeAgent\Screenshots\`. Flagged in its own doc
comment as the most storage- and privacy-sensitive feature in the agent —
that's why the interval is long by default rather than something like
every 30 seconds.

**`Core/NetworkUsageMonitor.cs`** — the per-process bandwidth feature.
Subscribes to the `Microsoft-Windows-Kernel-Network` ETW provider's TCP/UDP
send/recv events, which carry the owning process ID (something no public
.NET networking API exposes per-byte), accumulates counts in memory, and
`Flush()`es them to the log every 30 seconds rather than logging on every
single packet event. Like file-open tracking, this needs elevation and
degrades to a single failure event without it — there's no whole-machine
fallback if unelevated.

**`Core/DeviceActivityMonitor.cs`** — USB/device arrival and removal, via a
WMI event subscription (`Win32_DeviceChangeEvent`) rather than polling.
Straightforward: `EventType` code `2` means connected, `3` means
disconnected, anything else is logged as `device_change_other`.

**`Core/PrinterActivityMonitor.cs`** — polls `Win32_PrintJob` every 15
seconds and diffs the set of job IDs against what it saw last poll, rather
than using a WQL event subscription like `DeviceActivityMonitor` does for
USB. That's a deliberate, documented choice: print-job event queries are
known to be flaky across driver/spooler combinations, so polling a
snapshot is simpler and more reliable here even though it means a
very-short-lived job could theoretically be missed between two polls.

**`Core/IpLocationMonitor.cs`** — resolves a city-level location from the
machine's public IP via the free `ip-api.com` service, on a slow (default
hourly) timer plus one fire-and-forget call at startup. Explicitly not
GPS, and wrong under a VPN/proxy — the doc comment flags swapping this for
a paid/self-hosted GeoIP database before any real production use.

**`Core/SyncLoop.cs`** — the bridge to the backend. `CreateIfConfigured()`
returns `null` (events just accumulate in the local SQLite queue, unsent)
unless `EMPLOYEEAGENT_BACKEND_URL` is set. When active, `AgentContext`
ticks it every 4 seconds; each tick is a cheap `CountPendingAsync() == 0`
check when there's nothing queued, and sends immediately (no separate
time/count threshold needed anymore) when there is - this is what makes
delivery "small, frequent batches" rather than a long-delayed periodic
flush. On first sync it enrolls the device (`POST /api/v1/devices/enroll`)
and caches the returned API key to a local JSON file; every subsequent
sync calls `ActivityLogger.ReadPendingAsync()` for up to 100 rows, `POST`s
them as structured JSON to `/api/v1/ingest/events` with that key, and only
on a successful response calls `DeleteSyncedUpToAsync()` to remove exactly
those rows from the queue. A `401` response means the cached key was
rejected (e.g. the device got re-enrolled from another install) — it drops
the cached key
so the next tick re-enrolls from scratch instead of failing forever. Every
failure path here is caught and logged, never rethrown: this must never be
able to crash the agent or block monitors from continuing to queue events
locally just because the network or backend is unavailable - a failed
batch simply stays in the queue and is retried on the next tick.

**`EmployeeAgent.csproj`** — a `WinExe` targeting `net8.0-windows`, with
`UseWindowsForms` on (needed for the hidden-form/message-loop trick above)
and three package references: `System.Management` (WMI, for USB + printer
monitoring), `Microsoft.Diagnostics.Tracing.TraceEvent` (ETW, for the
file-open and network monitors), and `Microsoft.Data.Sqlite` (the local
event queue).

## `EmployeeAgent.Service/` — the anti-tamper Windows Service

**`Program.cs`** — minimal `Host.CreateApplicationBuilder` bootstrap that
registers itself as a Windows Service (`AddWindowsService`) and adds
`SessionSupervisor` as the one hosted background service. `AddWindowsService`
makes this correctly integrate with the Service Control Manager (start/stop
signals, event log) instead of just being a console app that happens to
stay open.

**`SessionSupervisor.cs`** — the actual supervision loop, and the entire
reason this service exists. Every 30 seconds it calls
`SessionInterop.GetActiveInteractiveSessionIds()` to list every logged-on
interactive session, checks which of those sessions already has an
`EmployeeAgent` process running in it, and for any session missing one,
calls `SessionInterop.TryLaunchProcessInSession()` to relaunch
`EmployeeAgent.exe` into it. This is the "OS-enforced" half of anti-tamper:
Windows' Service Control Manager restarts *this service* if it's killed
(via the `sc failure` policy `install-service.ps1` configures), and this
service in turn restarts *the agent* if that's killed — a two-layer chain,
neither layer capable of the other's job. The hardcoded `AgentExePath`
(`C:\Program Files\EmployeeAgent\EmployeeAgent.exe`) must match wherever
you actually publish the agent to.

**`SessionInterop.cs`** — the P/Invoke layer making cross-session process
launch possible at all. A `LocalSystem` service runs in Session 0, isolated
from every logged-on user's desktop — a plain `CreateProcess` call from
there either fails outright or launches invisibly in the wrong session. The
four-call chain here (`WTSQueryUserToken` → `DuplicateTokenEx` →
`CreateEnvironmentBlock` → `CreateProcessAsUser`) is the standard Windows
pattern for a SYSTEM process to impersonate a specific user's session well
enough to launch a process that actually lands on their visible desktop.
Each call does a distinct, necessary part of that impersonation — the doc
comment on this file explicitly warns against "simplifying" any of the four
away.

**`ActivityLog.cs`** — a deliberately duplicated (not shared) copy of the
same "resolve queue path, insert one row" logic
`EmployeeAgent/Core/ActivityLogger.cs` implements, writing into the exact
same SQLite file and `pending_events` table. The duplication is
intentional: this service has zero assembly dependency on the agent it
supervises, so a bug or crash in one can never take the other down. It only
ever inserts - `SyncLoop` (which only runs inside `EmployeeAgent.exe`) is
the sole reader/deleter of the queue. It also creates the table itself if
missing, since the service can start before any user session (and
therefore before `EmployeeAgent.exe`) does.

**`EmployeeAgent.Service.csproj`** — `UseWindowsService=true`, referencing
`Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Hosting.WindowsServices`,
and `Microsoft.Data.Sqlite`.

## `EmployeeAgent.NativeHost/` — the browser bridge

**`Program.cs`** — a single top-level-statements file implementing
Chrome/Edge's Native Messaging stdio protocol by hand: read a 4-byte
little-endian length prefix from stdin, read that many bytes as UTF-8 JSON,
write one JSON response back the same way, then exit. This process is
spawned fresh by the browser for every single message
(`chrome.runtime.sendNativeMessage` from `background.js`) — it is not a
long-running process and never loops waiting for a second message. On a
successful parse it extracts `url`/`title` and inserts a `website_visited`
row (with `{"url": ..., "title": ...}` as its structured details) straight
into the same shared SQLite queue the main agent writes to, using yet
another independently-duplicated copy of the insert logic (matching
`ActivityLog.cs` in the Service project, including creating the table if
this happens to be the very first writer to touch the file). Every failure
path is swallowed and still tries to write *some* response — the browser is
synchronously waiting on `sendNativeMessage`'s callback, so this must never
hang or crash without responding.

**`EmployeeAgent.NativeHost.csproj`** — plain console `Exe`, `net8.0` (not
`net8.0-windows` — this one has no WinForms/Win32 UI dependency at all),
`InvariantGlobalization` enabled since it does no culture-sensitive
formatting, referencing `Microsoft.Data.Sqlite` for the queue write.

## `browser-extension/` — the Manifest V3 extension

**`manifest.json`** — declares a Manifest V3 extension with `tabs` and
`nativeMessaging` permissions and `<all_urls>` host permissions (it needs
to see every navigation, not just a specific site), running `background.js`
as a service worker. No build step — this is loaded exactly as written,
either unpacked (`chrome://extensions` → Load unpacked) for testing, or
force-installed machine-wide via Group Policy for a real rollout.

**`background.js`** — listens for `chrome.tabs.onUpdated` (navigation
completing) and `chrome.tabs.onActivated` (switching to an already-loaded
tab), and for each, fires a one-shot `chrome.runtime.sendNativeMessage` to
`com.employeeagent.nativehost` (must match the host name
`install/register-native-host.ps1` registers) with the tab's URL, title,
and a UTC timestamp. One-shot messaging is a deliberate choice over a
persistent `connectNative()` port: MV3 service workers can be suspended by
the browser between navigations, which would silently drop a long-lived
port's connection — spawning the native host fresh per message sidesteps
that lifecycle problem entirely. If the native host isn't installed or
registered on this machine, the error is swallowed (`console.debug` only)
— `WindowActivityMonitor`'s regex-based domain fallback still covers that
window regardless.

## `install/` — one-time setup scripts

**`install-service.ps1`** — must run elevated. Creates the
`EmployeeAgentService` Windows Service pointing at a published
`EmployeeAgent.Service.exe`, then configures `sc failure` with a
never-resets-the-counter restart policy (`reset= 0`, three staggered
5-second restart actions) — this is the actual "OS-enforced" mechanism
referenced throughout: `SessionSupervisor.cs` is what restarts the *agent*,
this script's `sc failure` config is what makes Windows itself restart the
*service* if it's killed. It explicitly does not (and cannot, from a
script) configure the Group Policy restriction on who's allowed to run
`sc stop` — that's a tenant-specific Active Directory setting, out of
scope for anything this repo ships as code.

**`register-native-host.ps1`** — writes the native-messaging host manifest
JSON (pointing at a published `EmployeeAgent.NativeHost.exe`) and registers
it in the registry for both Chrome and Edge (`HKCU`, current-user only by
default) under the extension ID passed in — that ID has to be copied by
hand from `chrome://extensions` after loading the extension once, since
Chrome assigns it at load time.
