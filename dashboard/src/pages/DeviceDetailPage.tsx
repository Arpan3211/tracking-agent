import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Bar, BarChart, CartesianGrid, Cell, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api, buildQuery } from '../api/client'
import type { ActivityEventOut, ActivitySummaryOut, DeviceOut, Page, SessionOut } from '../api/types'
import { daysAgoIsoDate, formatDateTime, formatDuration, todayIsoDate } from '../lib/format'

const APP_USAGE_DAYS = 7
const EVENTS_PAGE_SIZE = 25

export function DeviceDetailPage() {
  const { deviceId } = useParams<{ deviceId: string }>()
  if (!deviceId) return null

  const deviceQuery = useQuery({
    queryKey: ['devices'],
    queryFn: () => api.get<DeviceOut[]>('/devices'),
    select: (devices) => devices.find((d) => d.id === deviceId),
  })

  const sessionsQuery = useQuery({
    queryKey: ['sessions', deviceId],
    queryFn: () => api.get<SessionOut[]>(`/devices/${deviceId}/sessions?limit=20`),
  })

  const from = daysAgoIsoDate(APP_USAGE_DAYS)
  const to = todayIsoDate()
  const summaryQuery = useQuery({
    queryKey: ['activity-summary', deviceId, from, to],
    queryFn: () => api.get<ActivitySummaryOut[]>(`/devices/${deviceId}/activity-summary?from=${from}&to=${to}`),
  })

  // Each day's top_apps is already capped to that day's top 10 (see backend
  // app/services/aggregation.py) - summing across days is a reasonable
  // overview but can undercount an app that was popular on one day and just
  // outside the top 10 on another. Fine for a "what dominates this device"
  // chart, not a source of truth for exact totals (the event timeline below
  // is that).
  const topApps = useMemo(() => {
    const totals = new Map<string, number>()
    for (const summary of summaryQuery.data ?? []) {
      for (const [app, seconds] of Object.entries(summary.top_apps ?? {})) {
        totals.set(app, (totals.get(app) ?? 0) + seconds)
      }
    }
    return Array.from(totals.entries())
      .map(([app, seconds]) => ({ app, hours: Math.round((seconds / 3600) * 100) / 100 }))
      .sort((a, b) => b.hours - a.hours)
      .slice(0, 10)
  }, [summaryQuery.data])

  const [eventTypeFilter, setEventTypeFilter] = useState('')
  const [eventFrom, setEventFrom] = useState('')
  const [eventTo, setEventTo] = useState('')
  const [page, setPage] = useState(1)

  const eventsQuery = useQuery({
    queryKey: ['events', deviceId, eventTypeFilter, eventFrom, eventTo, page],
    queryFn: () =>
      api.get<Page<ActivityEventOut>>(
        `/devices/${deviceId}/events${buildQuery({
          event_type: eventTypeFilter || undefined,
          from: eventFrom ? `${eventFrom}T00:00:00Z` : undefined,
          to: eventTo ? `${eventTo}T23:59:59Z` : undefined,
          page,
          page_size: EVENTS_PAGE_SIZE,
        })}`,
      ),
  })

  const device = deviceQuery.data
  const totalEventPages = eventsQuery.data ? Math.ceil(eventsQuery.data.total / EVENTS_PAGE_SIZE) : 1

  return (
    <div>
      <div className="page-header">
        <h1>{device?.machine_name ?? 'Device'}</h1>
      </div>
      {device && (
        <p className="muted" style={{ marginTop: -12, marginBottom: 20 }}>
          {device.os_version ?? 'Unknown OS'} · last seen{' '}
          {device.last_seen_at ? formatDateTime(device.last_seen_at) : 'never'}
        </p>
      )}

      <div className="card">
        <p className="card-title">App usage — last {APP_USAGE_DAYS} days</p>
        {topApps.length === 0 ? (
          <p className="muted">No app usage recorded in this range.</p>
        ) : (
          <ResponsiveContainer width="100%" height={Math.max(180, topApps.length * 32)}>
            <BarChart data={topApps} layout="vertical" margin={{ left: 8 }}>
              <CartesianGrid horizontal={false} stroke="var(--border)" />
              <XAxis type="number" tick={{ fontSize: 12, fill: 'var(--text-muted)' }} tickLine={false} />
              <YAxis
                type="category"
                dataKey="app"
                width={110}
                tick={{ fontSize: 12, fill: 'var(--text-primary)' }}
                tickLine={false}
                axisLine={false}
              />
              <Tooltip
                contentStyle={{ fontSize: 13, borderRadius: 6, border: '1px solid var(--border)' }}
                formatter={(value) => `${value}h`}
              />
              <Bar dataKey="hours" radius={[0, 4, 4, 0]}>
                {topApps.map((entry) => (
                  <Cell key={entry.app} fill="var(--series-blue)" />
                ))}
              </Bar>
            </BarChart>
          </ResponsiveContainer>
        )}
      </div>

      <div className="card section-gap">
        <p className="card-title">Session history</p>
        {sessionsQuery.isLoading ? (
          <p className="muted">Loading…</p>
        ) : (sessionsQuery.data ?? []).length === 0 ? (
          <p className="muted">No sessions recorded yet.</p>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>Login</th>
                <th>Logout</th>
                <th>Duration</th>
              </tr>
            </thead>
            <tbody>
              {(sessionsQuery.data ?? []).map((s) => (
                <tr key={s.id}>
                  <td>{formatDateTime(s.login_at)}</td>
                  <td>{s.logout_at ? formatDateTime(s.logout_at) : <span className="muted">open</span>}</td>
                  <td>{s.duration_seconds !== null ? formatDuration(s.duration_seconds) : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="card section-gap">
        <p className="card-title">Event timeline</p>

        <div className="filter-bar">
          <div className="form-field" style={{ marginBottom: 0 }}>
            <label>Event type</label>
            <input
              placeholder="e.g. login, file_opened"
              value={eventTypeFilter}
              onChange={(e) => {
                setPage(1)
                setEventTypeFilter(e.target.value)
              }}
            />
          </div>
          <div className="form-field" style={{ marginBottom: 0 }}>
            <label>From</label>
            <input
              type="date"
              value={eventFrom}
              onChange={(e) => {
                setPage(1)
                setEventFrom(e.target.value)
              }}
            />
          </div>
          <div className="form-field" style={{ marginBottom: 0 }}>
            <label>To</label>
            <input
              type="date"
              value={eventTo}
              onChange={(e) => {
                setPage(1)
                setEventTo(e.target.value)
              }}
            />
          </div>
        </div>

        {eventsQuery.isLoading ? (
          <p className="muted">Loading…</p>
        ) : (eventsQuery.data?.items ?? []).length === 0 ? (
          <p className="muted">No events match these filters.</p>
        ) : (
          <>
            <table className="data-table">
              <thead>
                <tr>
                  <th>Time</th>
                  <th>Event</th>
                  <th>Details</th>
                </tr>
              </thead>
              <tbody>
                {(eventsQuery.data?.items ?? []).map((e) => (
                  <tr key={e.id}>
                    <td>{formatDateTime(e.timestamp_utc)}</td>
                    <td>{e.event_type}</td>
                    <td className="muted">{e.details_raw ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            <div className="pagination-bar">
              <button className="btn" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
                ← Prev
              </button>
              <span>
                Page {page} of {totalEventPages} ({eventsQuery.data?.total ?? 0} events)
              </span>
              <button className="btn" disabled={page >= totalEventPages} onClick={() => setPage((p) => p + 1)}>
                Next →
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
