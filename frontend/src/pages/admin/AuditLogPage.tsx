import { useQuery } from '@tanstack/react-query'
import { api } from '../../api/client'
import type { AuditLogOut, UserOut } from '../../api/types'
import { formatDateTime } from '../../lib/format'
import { Card, DataTable, PageHeader, type DataTableColumn } from '../../components/theme'

export function AuditLogPage() {
  const auditQuery = useQuery({ queryKey: ['admin', 'audit-log'], queryFn: () => api.get<AuditLogOut[]>('/admin/audit-log') })
  const usersQuery = useQuery({ queryKey: ['admin', 'users'], queryFn: () => api.get<UserOut[]>('/admin/users') })

  const userById = new Map((usersQuery.data ?? []).map((u) => [u.id, u]))
  const entries = auditQuery.data ?? []

  const columns: DataTableColumn<AuditLogOut>[] = [
    { key: 'timestamp', header: 'Time', render: (entry) => formatDateTime(entry.timestamp) },
    { key: 'actor_user_id', header: 'Actor', render: (entry) => userById.get(entry.actor_user_id)?.email ?? entry.actor_user_id },
    { key: 'action', header: 'Action' },
    { key: 'target', header: 'Target', render: (entry) => <span className="muted">{entry.target ?? '—'}</span> },
  ]

  return (
    <div>
      <PageHeader title="Audit Log" />

      <Card>
        <DataTable
          columns={columns}
          rows={entries}
          rowKey={(entry) => entry.id}
          loading={auditQuery.isLoading}
          emptyMessage="No audit events recorded yet."
        />
      </Card>
    </div>
  )
}
