# Frontend — Code Explained

A file-by-file walkthrough of everything under `frontend/src/`. This covers
what each file does and why it's built the way it is — not a line-by-line
commentary. See [README.md](README.md) for setup/run instructions and the
architecture rationale this doc expands on.

## Entry point & routing

**`main.tsx`** — the actual app bootstrap: creates one `QueryClient`
(TanStack Query, with `retry: false` and `refetchOnWindowFocus: false` —
deliberately quiet defaults so a flaky request doesn't silently retry
against auth-sensitive endpoints, and so switching back to the browser tab
doesn't trigger a wave of refetches), wraps the tree in
`QueryClientProvider` and `BrowserRouter`, and renders `App`.

**`App.tsx`** — the route table. Every route except `/login` is nested
under `ProtectedRoute` (`src/auth/ProtectedRoute.tsx`), and the three
`/admin/*` routes are nested under a second `ProtectedRoute` with
`roles={['admin']}` — so admin-only gating is declared once at the routing
level rather than checked inside each admin page component. An unmatched
path (`*`) redirects to `/` rather than showing a 404, since every real
destination in this app requires auth anyway and would redirect there
itself.

## `auth/` — session state

**`auth/AuthContext.tsx`** — the single source of truth for "who's logged
in," as a React context. On mount it calls `GET /auth/me` exactly once; if
that fails (no valid session cookie), `user` just stays `null` — there's no
error state to handle here, an unauthenticated visitor is a completely
normal condition. `login()`/`logout()` call the corresponding endpoints and
update local state to match; the actual session lives entirely in the
httpOnly cookies the backend sets, this context is just a read-through
cache of "am I logged in and as whom."

**`auth/ProtectedRoute.tsx`** — a route-guard component (renders
`<Outlet/>` or redirects) rather than a hook, so it composes naturally with
`react-router`'s nested-route tree in `App.tsx`. Three states: still
loading the initial `/auth/me` check → a loading placeholder (not a
redirect — redirecting before that resolves would bounce a genuinely
logged-in user to `/login` for a flash); no user → redirect to `/login`;
user exists but `roles` was passed and doesn't include their role →
redirect to `/` (not `/login` — they're authenticated, just not authorized
for this specific route).

## `api/` — talking to the backend

**`api/client.ts`** — a hand-written `fetch` wrapper, not a generated
client or axios, centralizing three concerns every page would otherwise
have to duplicate: (1) attaching `X-CSRF-Token` on every mutating request,
read from the non-httpOnly `csrf_token` cookie — the frontend half of the
double-submit pattern described in `backend/app/core/csrf.py`; (2)
transparently retrying a request exactly once via `POST /auth/refresh` on
a `401`, with concurrent 401s coalesced into a single in-flight refresh
call (`refreshPromise`) rather than firing one refresh per failed request
if several widgets happen to query at once when the access token just
expired; (3) throwing a typed `ApiError` (carrying `status` and the parsed
body) so callers can branch on what went wrong instead of just catching a
generic error. Also exports small helpers used across pages: `buildQuery()`
(turns a params object into a query string, dropping empty/undefined
values and repeating array values as multiple same-name params — matches
FastAPI's expected list-query-param shape), `exportUrl()` (builds the
`/reports/export` download link used by `ReportsPage`), and
`websocketUrl()` (derives a `ws://`/`wss://` URL from the current page's
own origin, matching `http`/`https` — used by `AlertsPage`'s live feed).

**`api/types.ts`** — plain TypeScript interfaces mirroring every backend
Pydantic `*Out`/`Page` schema, by hand (no codegen). The file's own header
comment is explicit that these two have to be kept in sync manually if
either side changes.

## `layout/AppLayout.tsx`

The shell every protected page renders inside (via `<Outlet/>`): a left
sidebar with nav links (Dashboard/Reports/Alerts, plus an Admin section
shown only when `user.role === 'admin'`) and a footer showing the current
user's name, role, and a logout button. Purely presentational/structural —
it reads `useAuth()` for the user and delegates the actual auth actions to
that context rather than owning any auth logic itself.

## `lib/` — shared, non-component helpers

**`lib/format.ts`** — date/duration formatting used across every page:
`formatDuration()` (seconds → `"2h 14m"`/`"3m 5s"`, choosing units based on
magnitude rather than always showing every unit), `formatDateTime()` /
`formatDate()` (thin wrappers around `toLocaleString`/`toLocaleDateString`
so every page renders timestamps the same way, in the viewer's own locale),
and `todayIsoDate()`/`daysAgoIsoDate()` (the `YYYY-MM-DD` strings the
various date-range filters and `<input type="date">` fields need).

**`lib/icons.tsx`** — a small hand-rolled icon set (replacing the unused
default Vite template SVG assets) rather than an icon library dependency:
`base()` is a factory that takes just the inner `<path>`/`<rect>` elements
and returns a component with consistent `viewBox`, `stroke`, `size`, and
`strokeWidth` defaults, so every exported icon (`GridIcon`, `FileTextIcon`,
`BellIcon`, `ShieldIcon`, `UsersIcon`, `ListIcon`, `LogOutIcon`,
`MonitorIcon`, `ClockIcon`, `LayersIcon`, `CameraIcon`, …) is just its
unique path data plus that shared wrapper — adding a new icon means adding
one `base(<path .../>)` call, not a whole new component.

**`lib/eventMeta.ts`** — turns the agent's raw, machine-oriented event data
into what the dashboard actually displays, in two layers. First,
categorization: `CATEGORY_MAP` hand-maps every known `event_type` string
(from every monitor across `monitoring-agent/EmployeeAgent/Core/*.cs`) to
one of eleven `EventCategoryKey`s (`session`, `activity`, `web`, `files`,
`screenshots`, `network`, `printer`, `devices`, `location`, `security`,
`other`) with a label and a CSS-variable color each — this drives the
category filter pills and colored dots on `DeviceDetailPage`.
`eventTypesForCategory()` inverts that map (needed to build the
`event_type=` query filter sent to the backend). Second, humanization:
`summarizeEvent()` takes an event's structured `details` dict and returns
one clean display line per event type — e.g. `app_focus_change` becomes
`"chrome — GitHub"`, `network_usage` becomes `"chrome — ↑ 12.3 KB / ↓ 1.1 MB"`
via `formatBytes()`, `device_connected`/`device_disconnected` deliberately
return `"—"` since the agent only logs a raw WMI event code with no device
name to show. The file's own header comment flags this as needing to stay
in sync with the agent's per-event-type payload shape
(`monitoring-agent/EmployeeAgent/Core/*.cs`) if it ever adds a new event
type or changes an existing one's fields.

## `pages/` — one file per route

**`pages/LoginPage.tsx`** — a plain controlled email/password form. If
`useAuth()` already has a `user` (e.g. someone navigates back to `/login`
while already signed in), it renders a `<Navigate>` instead of the form,
redirecting to wherever `ProtectedRoute` originally bounced them from
(`location.state.from`) or `/` by default.

**`pages/DashboardPage.tsx`** — the landing page: three stat tiles (active
devices in the last 24h, average idle % over 14 days, alerts triggered
today), a stacked bar chart of active-vs-idle hours per day (Recharts), and
a device list table with online/offline status. "Online" here is
client-computed, not a stored flag — a device counts as online if
`last_seen_at` is within the last 24 hours. The 14-day trend chart fetches
each device's `activity-summary` in parallel (`Promise.all`) and merges by
date client-side, since the backend has no single "trend across all
devices" endpoint.

**`pages/DeviceDetailPage.tsx`** — the most complex page, and the one
`CODE_EXPLAINED.md` files elsewhere in the repo point to for the event
timeline: a horizontal bar chart of top apps by usage (summed across each
day's already-capped top-10 `top_apps`, which the component's own comment
flags as a reasonable overview but not an exact total for an app that
was popular one day and just outside the top 10 on another), a session
history table (login/logout/duration), an idle/pause-periods table (start/
end/duration per pause, from `GET /devices/{id}/idle-periods` — the
per-pause breakdown behind the "Idle time" stat tile's aggregate total),
and a paginated, filterable event timeline. The category filter pills need
per-category counts, which the backend doesn't expose directly —
`categoryCountsQuery` gets them cheaply by requesting `page_size=1` once
per known category (only `total` from the response is used) plus once
unfiltered, then derives the `other` count as the remainder rather than
querying for it, since there's no "event_type NOT IN (...)" filter on the
backend. Each event row can expand to show the raw `details` object
(pretty-printed JSON) alongside the humanized `summarizeEvent()` line, for
when the summary isn't enough.

**`pages/ReportsPage.tsx`** — a thin form (device, date range, format)
whose "Export" button is a plain `<a href>` pointing at
`exportUrl()`, not a fetch call — the browser handles the file download
natively via `Content-Disposition: attachment`, so there's no need to
manage a blob/download flow in React. Disabled until a device is selected.

**`pages/AlertsPage.tsx`** — a filterable alert list (status: unread/read/
all, plus device) with a live-updating twist: it opens a raw `WebSocket` to
`/ws/alerts` on mount and, on any incoming message, just invalidates every
`['alerts', ...]` TanStack Query cache entry rather than merging the pushed
alert into local state by hand — simpler than reconciling a pushed alert
against whichever read/device filter happens to be active right now, and
cheap enough at this data volume. Acknowledging an alert is a
`useMutation` that also invalidates the same query key on success.

**`pages/admin/UsersPage.tsx`** — user creation form plus a table of all
users with inline role/supervisor/active-status editing (each field change
fires its own `PATCH /admin/users/{id}` mutation immediately, not a
separate "save" step). The supervisor `<select>` options are just
`users.filter(role === 'supervisor')` computed client-side from the same
already-fetched user list.

**`pages/admin/PoliciesPage.tsx`** — same create-form-plus-editable-table
shape as `UsersPage`, for alert policies. `RULE_TYPES` hardcodes the two
backend `PolicyRuleType` values with a human-readable label and hint text
explaining what `threshold_value` means for each (mirrors the same
rule-type semantics documented in `backend/app/models/policy.py`) — only
`is_active` is editable inline after creation.

**`pages/admin/AuditLogPage.tsx`** — read-only: fetches the audit log plus
the full user list (to resolve `actor_user_id` to an email in the table),
no mutations at all. The simplest page in the app.
