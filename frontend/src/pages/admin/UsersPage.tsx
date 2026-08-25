import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../../api/client'
import type { UserOut, UserRole } from '../../api/types'

const ROLES: UserRole[] = ['employee', 'supervisor', 'hr', 'admin']

export function UsersPage() {
  const queryClient = useQueryClient()
  const usersQuery = useQuery({ queryKey: ['admin', 'users'], queryFn: () => api.get<UserOut[]>('/admin/users') })
  const users = usersQuery.data ?? []
  const supervisors = users.filter((u) => u.role === 'supervisor')

  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [fullName, setFullName] = useState('')
  const [role, setRole] = useState<UserRole>('employee')
  const [supervisorId, setSupervisorId] = useState('')
  const [formError, setFormError] = useState<string | null>(null)

  const createMutation = useMutation({
    mutationFn: () =>
      api.post<UserOut>('/admin/users', {
        email,
        password,
        full_name: fullName,
        role,
        supervisor_id: supervisorId || null,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['admin', 'users'] })
      setEmail('')
      setPassword('')
      setFullName('')
      setRole('employee')
      setSupervisorId('')
      setFormError(null)
    },
    onError: (err) => setFormError(err instanceof ApiError ? err.message : 'Failed to create user'),
  })

  const updateMutation = useMutation({
    mutationFn: (params: { id: string; changes: Partial<Pick<UserOut, 'role' | 'supervisor_id' | 'is_active'>> }) =>
      api.patch<UserOut>(`/admin/users/${params.id}`, params.changes),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['admin', 'users'] }),
  })

  function handleCreate(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    createMutation.mutate()
  }

  return (
    <div>
      <div className="page-header">
        <h1>Users</h1>
      </div>

      <div className="card">
        <p className="card-title">Add user</p>
        <form onSubmit={handleCreate}>
          <div className="form-row">
            <div className="form-field">
              <label>Email</label>
              <input type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
            </div>
            <div className="form-field">
              <label>Password</label>
              <input type="password" required value={password} onChange={(e) => setPassword(e.target.value)} />
            </div>
            <div className="form-field">
              <label>Full name</label>
              <input required value={fullName} onChange={(e) => setFullName(e.target.value)} />
            </div>
            <div className="form-field">
              <label>Role</label>
              <select value={role} onChange={(e) => setRole(e.target.value as UserRole)}>
                {ROLES.map((r) => (
                  <option key={r} value={r}>
                    {r}
                  </option>
                ))}
              </select>
            </div>
            <div className="form-field">
              <label>Supervisor</label>
              <select value={supervisorId} onChange={(e) => setSupervisorId(e.target.value)}>
                <option value="">None</option>
                {supervisors.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.full_name}
                  </option>
                ))}
              </select>
            </div>
            <button className="btn btn-primary" type="submit" disabled={createMutation.isPending}>
              {createMutation.isPending ? 'Creating…' : 'Create user'}
            </button>
          </div>
          {formError && <p className="error-text">{formError}</p>}
        </form>
      </div>

      <div className="card section-gap">
        <p className="card-title">All users</p>
        {usersQuery.isLoading ? (
          <p className="muted">Loading…</p>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Email</th>
                <th>Role</th>
                <th>Supervisor</th>
                <th>Active</th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id}>
                  <td>{u.full_name}</td>
                  <td>{u.email}</td>
                  <td>
                    <select
                      value={u.role}
                      onChange={(e) => updateMutation.mutate({ id: u.id, changes: { role: e.target.value as UserRole } })}
                    >
                      {ROLES.map((r) => (
                        <option key={r} value={r}>
                          {r}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td>
                    <select
                      value={u.supervisor_id ?? ''}
                      onChange={(e) =>
                        updateMutation.mutate({ id: u.id, changes: { supervisor_id: e.target.value || null } })
                      }
                    >
                      <option value="">None</option>
                      {supervisors
                        .filter((s) => s.id !== u.id)
                        .map((s) => (
                          <option key={s.id} value={s.id}>
                            {s.full_name}
                          </option>
                        ))}
                    </select>
                  </td>
                  <td>
                    <input
                      type="checkbox"
                      checked={u.is_active}
                      onChange={(e) => updateMutation.mutate({ id: u.id, changes: { is_active: e.target.checked } })}
                    />
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
