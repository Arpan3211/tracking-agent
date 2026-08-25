// Categorizes and humanizes the agent's event_type/details pairs for
// display. Keep in sync with the agent's per-event-type payload shape
// (see monitoring-agent/EmployeeAgent/Core/*.cs) if it ever adds a new
// event type or changes an existing one's fields.

export type EventCategoryKey =
  | 'session'
  | 'activity'
  | 'web'
  | 'files'
  | 'screenshots'
  | 'network'
  | 'printer'
  | 'devices'
  | 'location'
  | 'security'
  | 'other'

export interface EventCategory {
  key: EventCategoryKey
  label: string
  color: string
}

// This exact order (and these exact hues, defined in index.css) was run
// through the dataviz skill's six-check categorical palette validator -
// lightness band, chroma floor, CVD adjacency, normal-vision separation,
// and contrast - as a 10-slot set in this display order. Re-run the
// validator before reordering these or changing which hue any slot uses.
// 'security' deliberately does NOT reuse var(--status-critical) - a status
// color impersonating a category color is exactly the collision the
// validator's "documented palette only" check exists to prevent.
export const EVENT_CATEGORIES: EventCategory[] = [
  { key: 'session', label: 'Sessions', color: 'var(--series-blue)' },
  { key: 'activity', label: 'Activity & Apps', color: 'var(--series-orange)' },
  { key: 'web', label: 'Websites', color: 'var(--series-teal)' },
  { key: 'files', label: 'Files', color: 'var(--series-amber)' },
  { key: 'screenshots', label: 'Screenshots', color: 'var(--series-purple)' },
  { key: 'network', label: 'Network', color: 'var(--series-red)' },
  { key: 'printer', label: 'Printer', color: 'var(--series-green)' },
  { key: 'devices', label: 'USB / Devices', color: 'var(--series-pink)' },
  { key: 'location', label: 'Location', color: 'var(--series-emerald)' },
  { key: 'security', label: 'Security', color: 'var(--series-rust)' },
  { key: 'other', label: 'Other', color: 'var(--text-muted)' },
]

const CATEGORY_MAP: Record<string, EventCategoryKey> = {
  login: 'session',
  logout: 'session',
  agent_started: 'session',
  agent_stopped: 'session',
  device_enrolled: 'session',

  idle_start: 'activity',
  idle_end: 'activity',
  app_focus_change: 'activity',

  website_visited: 'web',

  file_created: 'files',
  file_renamed: 'files',
  file_changed: 'files',
  file_deleted: 'files',
  file_opened: 'files',
  file_open_tracking_failed: 'files',

  screenshot_captured: 'screenshots',
  screenshot_failed: 'screenshots',

  network_usage: 'network',
  network_monitor_failed: 'network',

  print_job_submitted: 'printer',
  print_job_completed: 'printer',

  device_connected: 'devices',
  device_disconnected: 'devices',
  device_change_other: 'devices',

  location_estimate: 'location',
  location_lookup_failed: 'location',

  screen_locked: 'security',
  screen_unlocked: 'security',
  remote_connect: 'security',
  remote_disconnect: 'security',
  watchdog_started: 'security',
  agent_restarted_by_watchdog: 'security',
  agent_restart_failed: 'security',
  service_started: 'security',
  service_stopped: 'security',
  session_agent_relaunched: 'security',
  session_agent_relaunch_failed: 'security',
}

export function categoryOf(eventType: string): EventCategory {
  const key = CATEGORY_MAP[eventType] ?? 'other'
  return EVENT_CATEGORIES.find((c) => c.key === key)!
}

export function eventTypesForCategory(category: EventCategoryKey): string[] {
  return Object.entries(CATEGORY_MAP)
    .filter(([, cat]) => cat === category)
    .map(([type]) => type)
}

export function formatEventTypeLabel(eventType: string): string {
  return eventType
    .split('_')
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ')
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`
}

/** Turns an event's structured `details` object into one clean,
 * human-readable line - this is what the UI shows by default instead of
 * the raw JSON object. */
export function summarizeEvent(eventType: string, details: Record<string, string> | null): string {
  const d = details ?? {}

  switch (eventType) {
    case 'app_focus_change':
      return d.title ? `${d.process ?? 'Unknown app'} — ${d.title}` : (d.process ?? 'Unknown app')
    case 'website_visited':
      return d.url ?? '—'
    case 'file_opened':
      return d.process ? `${d.path ?? '—'} (opened by ${d.process})` : (d.path ?? '—')
    case 'file_created':
    case 'file_renamed':
    case 'file_changed':
    case 'file_deleted':
      return d.path ?? Object.values(d)[0] ?? '—'
    case 'screenshot_captured': {
      const fileName = d.path?.split(/[\\/]/).pop()
      return fileName ? `${fileName}${d.monitor !== undefined ? ` (monitor ${d.monitor})` : ''}` : '—'
    }
    case 'network_usage': {
      const sent = Number(d.bytes_sent ?? 0)
      const received = Number(d.bytes_received ?? 0)
      const process = d.process ?? 'unattributed'
      return `${process} — ↑ ${formatBytes(sent)} / ↓ ${formatBytes(received)}`
    }
    case 'print_job_submitted':
    case 'print_job_completed':
      return [d.document, d.owner && `by ${d.owner}`, d.printer && `on ${d.printer}`].filter(Boolean).join(' ') || '—'
    case 'location_estimate':
      return [d.city, d.region, d.country].filter(Boolean).join(', ') || Object.values(d).join(', ') || '—'
    case 'login':
    case 'logout':
      return d.username ?? d.user ?? '—'
    case 'device_connected':
    case 'device_disconnected':
    case 'device_change_other':
      // The agent only logs the raw WMI Win32_DeviceChangeEvent code here
      // (see EmployeeAgent/Core/DeviceActivityMonitor.cs) with no device
      // name/description - the event_type badge already conveys what
      // happened, so there's nothing more useful to show per-row.
      return '—'
    default: {
      if (Object.keys(d).length === 0) return '—'
      return Object.entries(d)
        .map(([k, v]) => `${k}: ${v}`)
        .join(' · ')
    }
  }
}
