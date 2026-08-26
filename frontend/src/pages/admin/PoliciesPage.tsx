import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../../api/client'
import type { PolicyOut, PolicyRuleType } from '../../api/types'
import { formatDateTime } from '../../lib/format'
import { Button, Card, DataTable, PageHeader, type DataTableColumn } from '../../components/theme'

const RULE_TYPES: { value: PolicyRuleType; label: string; hint: string }[] = [
  { value: 'idle_threshold', label: 'Idle threshold', hint: 'minutes of continuous idle time' },
  { value: 'late_login', label: 'Late login', hint: 'minutes since midnight UTC (e.g. 570 = 09:30 UTC)' },
]

export function PoliciesPage() {
  const queryClient = useQueryClient()
  const policiesQuery = useQuery({ queryKey: ['admin', 'policies'], queryFn: () => api.get<PolicyOut[]>('/admin/policies') })
  const policies = policiesQuery.data ?? []

  const [name, setName] = useState('')
  const [ruleType, setRuleType] = useState<PolicyRuleType>('idle_threshold')
  const [thresholdValue, setThresholdValue] = useState(30)
  const [formError, setFormError] = useState<string | null>(null)

  const createMutation = useMutation({
    mutationFn: () => api.post<PolicyOut>('/admin/policies', { name, rule_type: ruleType, threshold_value: thresholdValue }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['admin', 'policies'] })
      setName('')
      setThresholdValue(30)
      setFormError(null)
    },
    onError: (err) => setFormError(err instanceof ApiError ? err.message : 'Failed to create policy'),
  })

  const toggleMutation = useMutation({
    mutationFn: (params: { id: string; is_active: boolean }) =>
      api.patch<PolicyOut>(`/admin/policies/${params.id}`, { is_active: params.is_active }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['admin', 'policies'] }),
  })

  function handleCreate(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    createMutation.mutate()
  }

  const activeRule = RULE_TYPES.find((r) => r.value === ruleType)!

  const columns: DataTableColumn<PolicyOut>[] = [
    { key: 'name', header: 'Name' },
    { key: 'rule_type', header: 'Rule', render: (p) => RULE_TYPES.find((r) => r.value === p.rule_type)?.label ?? p.rule_type },
    { key: 'threshold_value', header: 'Threshold' },
    { key: 'created_at', header: 'Created', render: (p) => formatDateTime(p.created_at) },
    {
      key: 'is_active',
      header: 'Active',
      render: (p) => (
        <input
          type="checkbox"
          checked={p.is_active}
          onChange={(e) => toggleMutation.mutate({ id: p.id, is_active: e.target.checked })}
        />
      ),
    },
  ]

  return (
    <div>
      <PageHeader title="Policies" />

      <Card title="New policy">
        <form onSubmit={handleCreate}>
          <div className="form-row">
            <div className="form-field">
              <label>Name</label>
              <input required value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Extended idle" />
            </div>
            <div className="form-field">
              <label>Rule type</label>
              <select value={ruleType} onChange={(e) => setRuleType(e.target.value as PolicyRuleType)}>
                {RULE_TYPES.map((r) => (
                  <option key={r.value} value={r.value}>
                    {r.label}
                  </option>
                ))}
              </select>
            </div>
            <div className="form-field">
              <label>Threshold ({activeRule.hint})</label>
              <input
                type="number"
                required
                min={0}
                value={thresholdValue}
                onChange={(e) => setThresholdValue(Number(e.target.value))}
              />
            </div>
            <Button variant="primary" type="submit" disabled={createMutation.isPending}>
              {createMutation.isPending ? 'Creating…' : 'Create policy'}
            </Button>
          </div>
          {formError && <p className="error-text">{formError}</p>}
        </form>
      </Card>

      <Card title="All policies" className="section-gap">
        <DataTable
          columns={columns}
          rows={policies}
          rowKey={(p) => p.id}
          loading={policiesQuery.isLoading}
          emptyMessage="No policies yet."
        />
      </Card>
    </div>
  )
}
