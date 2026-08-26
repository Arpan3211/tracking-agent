import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Bar, BarChart, CartesianGrid, Cell, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api, buildQuery } from '../api/client'
import type { ActivityEventOut, ActivitySummaryOut, DeviceOut, IdlePeriodOut, Page, SessionOut } from '../api/types'
import { daysAgoIsoDate, formatDateTime, formatDuration, todayIsoDate } from '../lib/format'
import {
  categoryOf,
  EVENT_CATEGORIES,
  eventTypesForCategory,
  formatEventTypeLabel,
  summarizeEvent,
  type EventCategoryKey,
} from '../lib/eventMeta'
import { ClockIcon, LayersIcon, CameraIcon, MonitorIcon, PauseIcon } from '../lib/icons'
import { Button, Card, DataTable, PageHeader, StatTile, type DataTableColumn } from '../components/theme'

const APP_USAGE_DAYS = 7
const EVENTS_PAGE_SIZE = 25
const KNOWN_CATEGORY_KEYS = EVENT_CATEGORIES.map((c) => c.key).filter((k) => k !== 'other') as EventCategoryKey[]

export function DeviceDetailPage() {
  const { deviceId = '' } = useParams<{ deviceId: string }>()
  const hasDeviceId = deviceId !== ''

  const deviceQuery = useQuery({
    queryKey: ['devices'],
    queryFn: () => api.get<DeviceOut[]>('/devices'),
    select: (devices) => devices.find((d) => d.id === deviceId),
  })

  const sessionsQuery = useQuery({
    queryKey: ['sessions', deviceId],
    queryFn: () => api.get<SessionOut[]>(`/devices/${deviceId}/sessions?limit=20`),
    enabled: hasDeviceId,
  })

  const from = daysAgoIsoDate(APP_USAGE_DAYS)
  const to = todayIsoDate()
  const summaryQuery = useQuery({
    queryKey: ['activity-summary', deviceId, from, to],
    queryFn: () => api.get<ActivitySummaryOut[]>(`/devices/${deviceId}/activity-summary?from=${from}&to=${to}`),
    enabled: hasDeviceId,
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

  const totalActiveSeconds = useMemo(
    () => (summaryQuery.data ?? []).reduce((sum, s) => sum + s.total_active_seconds, 0),
    [summaryQuery.data],
  )
  const totalIdleSeconds = useMemo(
    () => (summaryQuery.data ?? []).reduce((sum, s) => sum + s.total_idle_seconds, 0),
    [summaryQuery.data],
  )

  // The per-pause breakdown behind totalIdleSeconds above - individual
  // idle_start->idle_end pairs with their own duration, not just the daily
  // total (see backend app/api/v1/devices.py's get_idle_periods).
  const idlePeriodsQuery = useQuery({
    queryKey: ['idle-periods', deviceId, from, to],
    queryFn: () =>
      api.get<IdlePeriodOut[]>(
        `/devices/${deviceId}/idle-periods${buildQuery({ from: `${from}T00:00:00Z`, to: `${to}T23:59:59Z`, limit: 50 })}`,
      ),
    enabled: hasDeviceId,
  })

  // Counts per event category, used both for the filter pills and the quick
  // stat tiles - fetched as cheap page_size=1 requests (we only need `total`
  // from each), one per known category plus one unfiltered. `other` is
  // derived as the remainder rather than queried directly, since the
  // backend has no "event_type NOT IN (...)" filter.
  const categoryCountsQuery = useQuery({
    queryKey: ['event-category-counts', deviceId],
    queryFn: async () => {
      const [overall, ...perCategory] = await Promise.all([
        api.get<Page<ActivityEventOut>>(`/devices/${deviceId}/events${buildQuery({ page: 1, page_size: 1 })}`),
        ...KNOWN_CATEGORY_KEYS.map((key) =>
          api.get<Page<ActivityEventOut>>(
            `/devices/${deviceId}/events${buildQuery({
              event_type: eventTypesForCategory(key),
              page: 1,
              page_size: 1,
            })}`,
          ),
        ),
      ])
      const counts = Object.fromEntries(
        KNOWN_CATEGORY_KEYS.map((key, i) => [key, perCategory[i].total]),
      ) as Record<EventCategoryKey, number>
      const knownSum = Object.values(counts).reduce((a, b) => a + b, 0)
      counts.other = Math.max(0, overall.total - knownSum)
      return { total: overall.total, byCategory: counts }
    },
    enabled: hasDeviceId,
  })

  const [activeCategory, setActiveCategory] = useState<EventCategoryKey | 'all'>('all')
  const [eventFrom, setEventFrom] = useState('')
  const [eventTo, setEventTo] = useState('')
  const [page, setPage] = useState(1)
  const [expandedRows, setExpandedRows] = useState<Set<string>>(new Set())

  const eventsQuery = useQuery({
    queryKey: ['events', deviceId, activeCategory, eventFrom, eventTo, page],
    queryFn: () =>
      api.get<Page<ActivityEventOut>>(
        `/devices/${deviceId}/events${buildQuery({
          event_type: activeCategory !== 'all' && activeCategory !== 'other' ? eventTypesForCategory(activeCategory) : undefined,
          from: eventFrom ? `${eventFrom}T00:00:00Z` : undefined,
          to: eventTo ? `${eventTo}T23:59:59Z` : undefined,
          page,
          page_size: EVENTS_PAGE_SIZE,
        })}`,
      ),
    select: (result) =>
      activeCategory === 'other'
        ? { ...result, items: result.items.filter((e) => categoryOf(e.event_type).key === 'other') }
        : result,
    enabled: hasDeviceId,
  })

  if (!hasDeviceId) return null

  const device = deviceQuery.data
  const totalEventPages = eventsQuery.data ? Math.ceil(eventsQuery.data.total / EVENTS_PAGE_SIZE) : 1

  function toggleRow(id: string) {
    setExpandedRows((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const screenshotCount = categoryCountsQuery.data?.byCategory.screenshots ?? 0

  const sessionColumns: DataTableColumn<SessionOut>[] = [
    { key: 'login_at', header: 'Login', render: (s) => formatDateTime(s.login_at) },
    { key: 'logout_at', header: 'Logout', render: (s) => (s.logout_at ? formatDateTime(s.logout_at) : <span className="muted">open</span>) },
    { key: 'duration_seconds', header: 'Duration', render: (s) => (s.duration_seconds !== null ? formatDuration(s.duration_seconds) : '—') },
  ]

  const idlePeriodColumns: DataTableColumn<IdlePeriodOut>[] = [
    { key: 'start', header: 'Started', render: (p) => formatDateTime(p.start) },
    { key: 'end', header: 'Ended', render: (p) => (p.end ? formatDateTime(p.end) : <span className="muted">ongoing</span>) },
    { key: 'duration_seconds', header: 'Duration', render: (p) => (p.duration_seconds !== null ? formatDuration(p.duration_seconds) : '—') },
  ]

  const eventColumns: DataTableColumn<ActivityEventOut>[] = [
    { key: 'timestamp_utc', header: 'Time', width: '1%', render: (e) => <span style={{ whiteSpace: 'nowrap' }}>{formatDateTime(e.timestamp_utc)}</span> },
    {
      key: 'event_type',
      header: 'Event',
      render: (e) => {
        const category = categoryOf(e.event_type)
        return (
          <span className="event-badge">
            <span className="event-badge-dot" style={{ background: category.color }} />
            {formatEventTypeLabel(e.event_type)}
          </span>
        )
      },
    },
    {
      key: 'summary',
      header: 'Summary',
      render: (e) => {
        const isExpanded = expandedRows.has(e.id)
        return (
          <>
            <span className="event-summary" title={summarizeEvent(e.event_type, e.details)}>
              {summarizeEvent(e.event_type, e.details)}
            </span>
            {e.details && Object.keys(e.details).length > 0 && (
              <button className="event-raw-toggle" onClick={() => toggleRow(e.id)}>
                {isExpanded ? 'hide raw' : 'raw'}
              </button>
            )}
            {isExpanded && <div className="event-raw">{JSON.stringify(e.details, null, 2)}</div>}
          </>
        )
      },
    },
  ]

  return (
    <div>
      <PageHeader
        title={device?.machine_name ?? 'Device'}
        subtitle={
          device && (
            <>
              {device.os_version ?? 'Unknown OS'} · last seen{' '}
              {device.last_seen_at ? formatDateTime(device.last_seen_at) : 'never'}
            </>
          )
        }
      />

      <div className="stat-row">
        <StatTile
          icon={<ClockIcon size={20} />}
          label={`Active time (${APP_USAGE_DAYS}d)`}
          value={formatDuration(totalActiveSeconds)}
          iconBg="var(--accent-soft)"
          iconColor="var(--accent-ink)"
        />
        <StatTile
          icon={<PauseIcon size={20} />}
          label={`Idle time (${APP_USAGE_DAYS}d)`}
          value={formatDuration(totalIdleSeconds)}
          iconBg="var(--orange-light)"
          iconColor="var(--orange)"
        />
        <StatTile
          icon={<LayersIcon size={20} />}
          label="Total events tracked"
          value={categoryCountsQuery.data?.total ?? '—'}
          iconBg="color-mix(in srgb, var(--series-teal) 15%, transparent)"
          iconColor="var(--series-teal)"
        />
        <StatTile
          icon={<CameraIcon size={20} />}
          label="Screenshots"
          value={screenshotCount}
          iconBg="color-mix(in srgb, var(--series-purple) 15%, transparent)"
          iconColor="var(--series-purple)"
        />
        <StatTile
          icon={<MonitorIcon size={20} />}
          label="Sessions logged"
          value={sessionsQuery.data?.length ?? '—'}
          iconBg="var(--green-light)"
          iconColor="var(--green)"
        />
      </div>

      <Card title={`App usage — last ${APP_USAGE_DAYS} days`}>
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
                contentStyle={{ fontSize: 13, borderRadius: 8, border: '1px solid var(--border)' }}
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
      </Card>

      <Card title="Session history" className="section-gap">
        <DataTable
          columns={sessionColumns}
          rows={sessionsQuery.data ?? []}
          rowKey={(s) => s.id}
          loading={sessionsQuery.isLoading}
          emptyMessage="No sessions recorded yet."
        />
      </Card>

      <Card
        className="section-gap"
        title={
          <>
            Idle / pause periods
            <span className="card-title-sub">
              {idlePeriodsQuery.data
                ? `${idlePeriodsQuery.data.length} in the last ${APP_USAGE_DAYS}d, ${formatDuration(totalIdleSeconds)} total`
                : ''}
            </span>
          </>
        }
      >
        <DataTable
          columns={idlePeriodColumns}
          rows={idlePeriodsQuery.data ?? []}
          rowKey={(p) => p.start}
          loading={idlePeriodsQuery.isLoading}
          emptyMessage="No idle periods recorded in this range."
        />
      </Card>

      <Card
        className="section-gap"
        title={
          <>
            Event timeline
            <span className="card-title-sub">{eventsQuery.data ? `${eventsQuery.data.total} matching` : ''}</span>
          </>
        }
      >
        <div className="category-pills">
          <button
            className={`category-pill${activeCategory === 'all' ? ' active' : ''}`}
            onClick={() => {
              setPage(1)
              setActiveCategory('all')
            }}
          >
            All
            <span className="category-pill-count">{categoryCountsQuery.data?.total ?? '…'}</span>
          </button>
          {EVENT_CATEGORIES.map((cat) => {
            const count = categoryCountsQuery.data?.byCategory[cat.key] ?? 0
            if (categoryCountsQuery.isSuccess && count === 0) return null
            return (
              <button
                key={cat.key}
                className={`category-pill${activeCategory === cat.key ? ' active' : ''}`}
                onClick={() => {
                  setPage(1)
                  setActiveCategory(cat.key)
                }}
              >
                <span className="category-pill-dot" style={{ background: cat.color }} />
                {cat.label}
                <span className="category-pill-count">{categoryCountsQuery.isLoading ? '…' : count}</span>
              </button>
            )
          })}
        </div>

        <div className="filter-bar">
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

        <DataTable
          columns={eventColumns}
          rows={eventsQuery.data?.items ?? []}
          rowKey={(e) => e.id}
          loading={eventsQuery.isLoading}
          emptyMessage="No events match these filters."
        />

        {(eventsQuery.data?.items ?? []).length > 0 && (
          <div className="pagination-bar">
            <Button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              ← Prev
            </Button>
            <span>
              Page {page} of {totalEventPages} ({eventsQuery.data?.total ?? 0} events)
            </span>
            <Button disabled={page >= totalEventPages} onClick={() => setPage((p) => p + 1)}>
              Next →
            </Button>
          </div>
        )}
      </Card>
    </div>
  )
}
