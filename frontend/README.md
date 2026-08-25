# Employee Agent — Frontend

## What this is

React + TypeScript dashboard (Vite) for the Employee Agent monitoring
stack: the web UI where supervisors/HR/Admin log in and view device
activity, sessions, reports, and alerts synced by the backend from the
Windows agent.

See the root [README.md](../README.md) for how this fits together with the
agent and the backend, and [CODE_EXPLAINED.md](CODE_EXPLAINED.md) for a
file-by-file walkthrough of everything in this folder.

## Running it locally

Unlike the backend, this doesn't run in Docker - there's no frontend
service in the root `docker-compose.yml` (see "Why it's set up to run this
way" below). It runs directly with Node.

**Prerequisite:** the backend must already be running (`docker compose up
-d db` then `docker compose up -d api` from the repo root - see
[backend/README.md](../backend/README.md)) and migrated/seeded, since the
dashboard has nothing to show and no one to log in as otherwise.

**1. Install dependencies:**

```bash
cd frontend
npm install
```

**2. Start the dev server:**

```bash
npm run dev
```

**3. Open it and log in:**

Open `http://localhost:5173` and log in with the admin credentials from
`backend/.env` (`SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD` - see
[backend/README.md](../backend/README.md#running-it-locally)).

**Stopping it:** `Ctrl+C` in the terminal running `npm run dev`. There's no
container or background process to separately stop or clean up - once that
terminal exits, nothing is running.

**Building for production:**

```bash
npm run build
```

Outputs a static bundle to `frontend/dist/` (gitignored) - serve it from
any static host or reverse proxy alongside the API. There's no
frontend-specific Docker service defined yet, matching the original scope
of the Compose file being API + DB only.

## Why it's set up to run this way locally

- **Vite's dev server, not Docker** - a frontend build has no state to keep
  consistent across machines the way a database does (that's the whole
  reason the backend uses Docker Compose - see
  [backend/README.md](../backend/README.md#why-its-set-up-to-run-this-way-locally)).
  `npm install && npm run dev` is already fully reproducible via
  `package-lock.json`, so containerizing it would only add a build step and
  a slower feedback loop for no real benefit during development.
- **The dev server proxies `/api` to `http://localhost:8000`** (see
  `vite.config.ts`) so the browser sees the dashboard (`:5173`) and the API
  (`:8000`) as the **same origin**. This isn't just convenience - auth here
  uses httpOnly cookies with `SameSite=Lax`. If the dashboard called the API
  cross-origin directly instead, those cookies would need to be cross-site,
  which complicates the CSRF double-submit story (see
  `src/api/client.ts` and `backend/app/core/csrf.py`) for no benefit in a
  local setup where both are on `localhost` anyway. The proxy sidesteps all
  of that by making it a non-issue.
- **`credentials: 'include'` on every fetch** (`src/api/client.ts`) is what
  actually sends the httpOnly cookies with each request - this only works
  reliably same-origin, reinforcing why the proxy setup matters rather than
  being an arbitrary choice.
- **No `.env` file for the frontend** - unlike the backend, there's nothing
  here that differs between local dev and this deployment target (no API
  keys, no secrets); the only environment-dependent value is the API's base
  URL, and that's solved by the same-origin proxy above rather than a
  configurable env var.

## Tech stack

React 19 · TypeScript · Vite (dev server + build) · React Router v7 ·
TanStack Query (server-state caching/refetching) · Recharts (charts) ·
Oxlint (linting).

## Architecture notes

- **Routing** (`src/App.tsx`): every route except `/login` is wrapped in
  `ProtectedRoute` (`src/auth/ProtectedRoute.tsx`), which redirects to
  `/login` if there's no authenticated user, and redirects Admin-only
  routes (`/admin/*`) back to `/` for non-Admins - client-side gating only,
  mirroring but not replacing the backend's own `require_roles` checks.
- **Auth state** (`src/auth/AuthContext.tsx`) is a single React context
  populated by calling `GET /auth/me` once on mount - if that fails (no
  valid session cookie), the user is simply `null` and every protected
  route redirects to `/login`. `login()`/`logout()` just call the
  corresponding API endpoints and update this local state; the actual
  session lives in the httpOnly cookies the backend sets, not in anything
  the frontend stores itself.
- **API client** (`src/api/client.ts`) is a thin `fetch` wrapper, not a
  generated client or axios - it centralizes three things so no individual
  page has to think about them: attaching the CSRF header on mutating
  requests, transparently retrying once via `/auth/refresh` on a 401
  (coalescing concurrent refreshes into a single in-flight request instead
  of firing one per failed request), and throwing a typed `ApiError` so
  callers can branch on `status`/`body`.
- **Data fetching uses TanStack Query**, not raw `useEffect` + `useState`
  per page - it owns caching, refetching, and loading/error state for
  server data uniformly across pages.
- **`src/lib/`** holds cross-page helpers that aren't components: `icons.tsx`
  (the icon set, replacing the default Vite template's unused SVG assets),
  `eventMeta.ts` (categorizes/humanizes the agent's `event_type`/`details`
  pairs for display - keep in sync with the agent's per-event-type payload
  shape if it ever adds a new event type), and `format.ts` (date/duration
  formatting shared across pages).
- **`src/pages/`** is one file per route, plus an `admin/` subfolder for the
  three Admin-only pages - flat by design at this size; introduce nested
  feature folders only if a page-with-subcomponents actually needs one, not
  preemptively.
