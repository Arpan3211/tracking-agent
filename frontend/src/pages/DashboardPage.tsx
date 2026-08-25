import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import { api } from '../api/client'
import type { ActivitySummaryOut, DeviceOut, AlertOut } from '../api/types'
import { daysAgoIsoDate, formatDateTime, todayIsoDate } from '../lib/format'
import { BellIcon, MonitorIcon, ClockIcon } from '../lib/icons'

const TREND_DAYS = 14

interface TrendPoint {
  date: string
  activeHours: number
  idleHours: number
}

export function DashboardPage() {
  const devicesQuery = useQuery({ queryKey: ['devices'], queryFn: () => api.get<DeviceOut[]>('/devices') })
  const alertsQuery = useQuery({ queryKey: ['alerts', 'recent'], queryFn: () => api.get<AlertOut[]>('/alerts?limit=200') })

  const from = daysAgoIsoDate(TREND_DAYS)
  const to = todayIsoDate()
  const deviceIds = useMemo(() => (devicesQuery.data ?? []).map((d) => d.id), [devicesQuery.data])

  const summariesQuery = useQuery({
    queryKey: ['activity-summaries', deviceIds, from, to],
    queryFn: async () => {
      const results = await Promise.all(
        deviceIds.map((id) =>
          api.get<ActivitySummaryOut[]>(`/devices/${id}/activity-summary?from=${from}&to=${to}`),
        ),
      )
      return results.flat()
    },
    enabled: deviceIds.length > 0,
  })

  const summaries = summariesQuery.data ?? []
  const devices = devicesQuery.data ?? []
  const alerts = alertsQuery.data ?? []

  const activeDevicesCount = devices.filter((d) => {
    if (!d.last_seen_at) return false
    return Date.now() - new Date(d.last_seen_at).getTime() < 24 * 60 * 60 * 1000
  }).length

  const avgIdlePct = (() => {
    if (summaries.length === 0) return null
    const totalActive = summaries.reduce((sum, s) => sum + s.total_active_seconds, 0)
    const totalIdle = summaries.reduce((sum, s) => sum + s.total_idle_seconds, 0)
    const total = totalActive + totalIdle
    return total > 0 ? Math.round((totalIdle / total) * 100) : 0
  })()

  const alertsToday = alerts.filter((a) => a.triggered_at.slice(0, 10) === to).length

  const trendData: TrendPoint[] = useMemo(() => {
    const byDate = new Map<string, { active: number; idle: number }>()
    for (const s of summaries) {
      const entry = byDate.get(s.date) ?? { active: 0, idle: 0 }
      entry.active += s.total_active_seconds
      entry.idle += s.total_idle_seconds
      byDate.set(s.date, entry)
    }
    return Array.from(byDate.entries())
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([date, { active, idle }]) => ({
        date,
        activeHours: Math.round((active / 3600) * 10) / 10,
        idleHours: Math.round((idle / 3600) * 10) / 10,
      }))
  }, [summaries])

  return (
    <div>
      <div className="page-header">
        <h1>Dashboard</h1>
      </div>

      <div className="stat-row">
        <div className="stat-tile">
          <span
            className="stat-tile-icon"
            style={{ background: 'color-mix(in srgb, var(--series-blue) 15%, transparent)', color: 'var(--series-blue)' }}
          >
            <MonitorIcon size={20} />
          </span>
          <div className="stat-tile-body">
            <div className="stat-tile-label">Active devices (24h)</div>
            <div className="stat-tile-value">{devicesQuery.isLoading ? '—' : activeDevicesCount}</div>
          </div>
        </div>
        <div className="stat-tile">
          <span
            className="stat-tile-icon"
            style={{ background: 'color-mix(in srgb, var(--series-amber) 18%, transparent)', color: '#a16207' }}
          >
            <ClockIcon size={20} />
          </span>
          <div className="stat-tile-body">
            <div className="stat-tile-label">Avg idle % ({TREND_DAYS}d)</div>
            <div className="stat-tile-value">{avgIdlePct === null ? '—' : `${avgIdlePct}%`}</div>
          </div>
        </div>
        <div className="stat-tile">
          <span
            className="stat-tile-icon"
            style={{ background: 'color-mix(in srgb, var(--status-critical) 12%, transparent)', color: 'var(--status-critical)' }}
          >
            <BellIcon size={20} />
          </span>
          <div className="stat-tile-body">
            <div className="stat-tile-label">Alerts today</div>
            <div className="stat-tile-value">{alertsQuery.isLoading ? '—' : alertsToday}</div>
          </div>
        </div>
      </div>

      <div className="card">
        <p className="card-title">Active vs idle time — last {TREND_DAYS} days</p>
        {trendData.length === 0 ? (
          <p className="muted">No activity data yet for this range.</p>
        ) : (
          <ResponsiveContainer width="100%" height={260}>
            <BarChart data={trendData} barCategoryGap={4}>
              <CartesianGrid vertical={false} stroke="var(--border)" />
              <XAxis dataKey="date" tick={{ fontSize: 12, fill: 'var(--text-muted)' }} tickLine={false} />
              <YAxis
                tick={{ fontSize: 12, fill: 'var(--text-muted)' }}
                tickLine={false}
                axisLine={false}
                label={{ value: 'hours', angle: -90, position: 'insideLeft', fontSize: 12, fill: 'var(--text-muted)' }}
              />
              <Tooltip
                contentStyle={{ fontSize: 13, borderRadius: 6, border: '1px solid var(--border)' }}
                formatter={(value) => `${value}h`}
              />
              <Legend wrapperStyle={{ fontSize: 13 }} />
              <Bar dataKey="activeHours" name="Active" stackId="a" fill="var(--series-blue)" radius={[0, 0, 0, 0]} />
              <Bar dataKey="idleHours" name="Idle" stackId="a" fill="var(--series-orange)" radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        )}
      </div>

      <div className="card section-gap">
        <p className="card-title">Devices</p>
        {devicesQuery.isLoading ? (
          <p className="muted">Loading…</p>
        ) : devices.length === 0 ? (
          <p className="muted">No devices enrolled yet.</p>
        ) : (
          <div className="table-scroll">
          <table className="data-table">
            <thead>
              <tr>
                <th>Machine</th>
                <th>Status</th>
                <th>OS</th>
                <th>Last seen</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {devices.map((d) => {
                const online = d.last_seen_at ? Date.now() - new Date(d.last_seen_at).getTime() < 24 * 60 * 60 * 1000 : false
                return (
                <tr key={d.id}>
                  <td>
                    <Link to={`/devices/${d.id}`}>{d.machine_name}</Link>
                  </td>
                  <td>
                    <span className={`chip ${online ? 'chip-acknowledged' : 'chip-unread'}`}>
                      <span className="chip-dot" /> {online ? 'Online' : 'Offline'}
                    </span>
                  </td>
                  <td>{d.os_version ?? '—'}</td>
                  <td>{d.last_seen_at ? formatDateTime(d.last_seen_at) : 'never'}</td>
                  <td>
                    <Link to={`/devices/${d.id}`}>View →</Link>
                  </td>
                </tr>
                )
              })}
            </tbody>
          </table>
          </div>
        )}
      </div>
    </div>
  )
}
