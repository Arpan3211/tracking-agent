import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, buildQuery, websocketUrl } from '../api/client'
import type { AlertOut, DeviceOut } from '../api/types'
import { formatDateTime } from '../lib/format'
import { Button, Card, Chip, DataTable, PageHeader, type DataTableColumn } from '../components/theme'

type ReadFilter = 'all' | 'unread' | 'read'

export function AlertsPage() {
  const queryClient = useQueryClient()
  const devicesQuery = useQuery({ queryKey: ['devices'], queryFn: () => api.get<DeviceOut[]>('/devices') })
  const devices = devicesQuery.data ?? []

  const [readFilter, setReadFilter] = useState<ReadFilter>('unread')
  const [deviceId, setDeviceId] = useState('')

  const queryKey = ['alerts', readFilter, deviceId]
  const alertsQuery = useQuery({
    queryKey,
    queryFn: () =>
      api.get<AlertOut[]>(
        `/alerts${buildQuery({
          is_read: readFilter === 'all' ? undefined : readFilter === 'read',
          device_id: deviceId || undefined,
        })}`,
      ),
  })

  // Live updates: the backend broadcasts every newly-created alert over this
  // socket (see backend app/api/v1/websocket.py). Rather than merge the
  // pushed alert into local state by hand (and have to reconcile it against
  // whatever filter is active), just invalidate every ['alerts', ...] query
  // - cheap at this data volume and avoids filter-matching logic living in
  // two places.
  useEffect(() => {
    const ws = new WebSocket(websocketUrl('/ws/alerts'))
    ws.onmessage = () => {
      void queryClient.invalidateQueries({ queryKey: ['alerts'] })
    }
    return () => ws.close()
  }, [queryClient])

  const acknowledgeMutation = useMutation({
    mutationFn: (alertId: string) => api.post<AlertOut>(`/alerts/${alertId}/acknowledge`),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['alerts'] })
    },
  })

  const deviceNameById = new Map(devices.map((d) => [d.id, d.machine_name]))
  const alerts = alertsQuery.data ?? []

  const columns: DataTableColumn<AlertOut>[] = [
    {
      key: 'status',
      header: 'Status',
      render: (a) =>
        a.is_read ? (
          <Chip tone="acknowledged" dot>
            Acknowledged
          </Chip>
        ) : (
          <Chip tone="unread" dot>
            Unread
          </Chip>
        ),
    },
    { key: 'device_id', header: 'Device', render: (a) => deviceNameById.get(a.device_id) ?? a.device_id },
    { key: 'message', header: 'Message' },
    { key: 'triggered_at', header: 'Triggered', render: (a) => formatDateTime(a.triggered_at) },
    {
      key: 'actions',
      header: '',
      render: (a) =>
        !a.is_read && (
          <Button disabled={acknowledgeMutation.isPending} onClick={() => acknowledgeMutation.mutate(a.id)}>
            Acknowledge
          </Button>
        ),
    },
  ]

  return (
    <div>
      <PageHeader title="Alerts" />

      <div className="filter-bar">
        <div className="form-field" style={{ marginBottom: 0 }}>
          <label>Status</label>
          <select value={readFilter} onChange={(e) => setReadFilter(e.target.value as ReadFilter)}>
            <option value="unread">Unread</option>
            <option value="read">Acknowledged</option>
            <option value="all">All</option>
          </select>
        </div>
        <div className="form-field" style={{ marginBottom: 0 }}>
          <label>Device</label>
          <select value={deviceId} onChange={(e) => setDeviceId(e.target.value)}>
            <option value="">All devices</option>
            {devices.map((d) => (
              <option key={d.id} value={d.id}>
                {d.machine_name}
              </option>
            ))}
          </select>
        </div>
      </div>

      <Card>
        <DataTable
          columns={columns}
          rows={alerts}
          rowKey={(a) => a.id}
          loading={alertsQuery.isLoading}
          emptyMessage="No alerts match these filters."
        />
      </Card>
    </div>
  )
}
