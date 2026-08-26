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
import { Card, Chip, DataTable, PageHeader, StatTile, type DataTableColumn } from '../components/theme'

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

  const deviceColumns: DataTableColumn<DeviceOut>[] = [
    { key: 'machine_name', header: 'Machine', render: (d) => <Link to={`/devices/${d.id}`}>{d.machine_name}</Link> },
    {
      key: 'status',
      header: 'Status',
      render: (d) => {
        const online = d.last_seen_at ? Date.now() - new Date(d.last_seen_at).getTime() < 24 * 60 * 60 * 1000 : false
        return (
          <Chip tone={online ? 'acknowledged' : 'unread'} dot>
            {online ? 'Online' : 'Offline'}
          </Chip>
        )
      },
    },
    { key: 'os_version', header: 'OS', render: (d) => d.os_version ?? '—' },
    { key: 'last_seen_at', header: 'Last seen', render: (d) => (d.last_seen_at ? formatDateTime(d.last_seen_at) : 'never') },
    { key: 'link', header: '', render: (d) => <Link to={`/devices/${d.id}`}>View →</Link> },
  ]

  return (
    <div>
      <PageHeader title="Dashboard" />

      <div className="stat-row">
        <StatTile
          icon={<MonitorIcon size={20} />}
          label="Active devices (24h)"
          value={devicesQuery.isLoading ? '—' : activeDevicesCount}
          iconBg="var(--accent-soft)"
          iconColor="var(--accent-ink)"
        />
        <StatTile
          icon={<ClockIcon size={20} />}
          label={`Avg idle % (${TREND_DAYS}d)`}
          value={avgIdlePct === null ? '—' : `${avgIdlePct}%`}
          iconBg="var(--orange-light)"
          iconColor="var(--orange)"
        />
        <StatTile
          icon={<BellIcon size={20} />}
          label="Alerts today"
          value={alertsQuery.isLoading ? '—' : alertsToday}
          iconBg="var(--red-light)"
          iconColor="var(--red)"
        />
      </div>

      <Card title={`Active vs idle time — last ${TREND_DAYS} days`}>
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
      </Card>

      <Card title="Devices" className="section-gap">
        <DataTable
          columns={deviceColumns}
          rows={devices}
          rowKey={(d) => d.id}
          loading={devicesQuery.isLoading}
          emptyMessage="No devices enrolled yet."
        />
      </Card>
    </div>
  )
}
