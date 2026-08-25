import { useQuery } from '@tanstack/react-query'
import { api } from '../../api/client'
import type { AuditLogOut, UserOut } from '../../api/types'
import { formatDateTime } from '../../lib/format'

export function AuditLogPage() {
  const auditQuery = useQuery({ queryKey: ['admin', 'audit-log'], queryFn: () => api.get<AuditLogOut[]>('/admin/audit-log') })
  const usersQuery = useQuery({ queryKey: ['admin', 'users'], queryFn: () => api.get<UserOut[]>('/admin/users') })

  const userById = new Map((usersQuery.data ?? []).map((u) => [u.id, u]))
  const entries = auditQuery.data ?? []

  return (
    <div>
      <div className="page-header">
        <h1>Audit Log</h1>
      </div>

      <div className="card">
        {auditQuery.isLoading ? (
          <p className="muted">Loading…</p>
        ) : entries.length === 0 ? (
          <p className="muted">No audit events recorded yet.</p>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>Time</th>
                <th>Actor</th>
                <th>Action</th>
                <th>Target</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((entry) => (
                <tr key={entry.id}>
                  <td>{formatDateTime(entry.timestamp)}</td>
                  <td>{userById.get(entry.actor_user_id)?.email ?? entry.actor_user_id}</td>
                  <td>{entry.action}</td>
                  <td className="muted">{entry.target ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}
