import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, exportUrl } from '../api/client'
import type { DeviceOut } from '../api/types'
import { daysAgoIsoDate, todayIsoDate } from '../lib/format'
import { Button, Card, PageHeader } from '../components/theme'

export function ReportsPage() {
  const devicesQuery = useQuery({ queryKey: ['devices'], queryFn: () => api.get<DeviceOut[]>('/devices') })
  const devices = devicesQuery.data ?? []

  const [deviceId, setDeviceId] = useState('')
  const [from, setFrom] = useState(daysAgoIsoDate(30))
  const [to, setTo] = useState(todayIsoDate())
  const [format, setFormat] = useState<'csv' | 'xlsx'>('csv')

  const selectedDevice = devices.find((d) => d.id === deviceId)
  const canExport = deviceId !== ''

  return (
    <div>
      <PageHeader title="Reports" />

      <Card title="Export activity data">
        <div className="form-row">
          <div className="form-field">
            <label>Device</label>
            <select value={deviceId} onChange={(e) => setDeviceId(e.target.value)}>
              <option value="">Select a device…</option>
              {devices.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.machine_name}
                </option>
              ))}
            </select>
          </div>

          <div className="form-field">
            <label>From</label>
            <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
          </div>

          <div className="form-field">
            <label>To</label>
            <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
          </div>

          <div className="form-field">
            <label>Format</label>
            <select value={format} onChange={(e) => setFormat(e.target.value as 'csv' | 'xlsx')}>
              <option value="csv">CSV</option>
              <option value="xlsx">Excel (XLSX)</option>
            </select>
          </div>

          {canExport ? (
            <a
              className="btn btn-primary"
              href={exportUrl(deviceId, format, `${from}T00:00:00Z`, `${to}T23:59:59Z`)}
              target="_blank"
              rel="noreferrer"
            >
              Export
            </a>
          ) : (
            <Button variant="primary" disabled>
              Export
            </Button>
          )}
        </div>

        {selectedDevice && (
          <p className="muted">
            Exports raw activity events for {selectedDevice.machine_name} between {from} and {to}. The action is
            recorded in the audit log.
          </p>
        )}
      </Card>
    </div>
  )
}
