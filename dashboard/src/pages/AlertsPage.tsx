import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, buildQuery, websocketUrl } from '../api/client'
import type { AlertOut, DeviceOut } from '../api/types'
import { formatDateTime } from '../lib/format'

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

  return (
    <div>
      <div className="page-header">
        <h1>Alerts</h1>
      </div>

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

      <div className="card">
        {alertsQuery.isLoading ? (
          <p className="muted">Loading…</p>
        ) : alerts.length === 0 ? (
          <p className="muted">No alerts match these filters.</p>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>Status</th>
                <th>Device</th>
                <th>Message</th>
                <th>Triggered</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {alerts.map((a) => (
                <tr key={a.id}>
                  <td>
                    {a.is_read ? (
                      <span className="chip chip-acknowledged">
                        <span className="chip-dot" /> Acknowledged
                      </span>
                    ) : (
                      <span className="chip chip-unread">
                        <span className="chip-dot" /> Unread
                      </span>
                    )}
                  </td>
                  <td>{deviceNameById.get(a.device_id) ?? a.device_id}</td>
                  <td>{a.message}</td>
                  <td>{formatDateTime(a.triggered_at)}</td>
                  <td>
                    {!a.is_read && (
                      <button
                        className="btn"
                        disabled={acknowledgeMutation.isPending}
                        onClick={() => acknowledgeMutation.mutate(a.id)}
                      >
                        Acknowledge
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}
