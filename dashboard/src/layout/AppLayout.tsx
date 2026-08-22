import { NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'

export function AppLayout() {
  const { user, logout } = useAuth()
  if (!user) return null

  const isAdmin = user.role === 'admin'

  return (
    <div className="app-shell">
      <nav className="sidebar">
        <div className="sidebar-brand">Employee Agent</div>

        <NavLink to="/" end className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
          Dashboard
        </NavLink>
        <NavLink to="/reports" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
          Reports
        </NavLink>
        <NavLink to="/alerts" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
          Alerts
        </NavLink>

        {isAdmin && (
          <>
            <div className="sidebar-section-label">Admin</div>
            <NavLink to="/admin/users" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
              Users
            </NavLink>
            <NavLink to="/admin/policies" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
              Policies
            </NavLink>
            <NavLink to="/admin/audit-log" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
              Audit Log
            </NavLink>
          </>
        )}

        <div className="sidebar-footer">
          <div>{user.full_name}</div>
          <div className="muted">{user.role}</div>
          <button className="btn" style={{ marginTop: 10, width: '100%' }} onClick={() => void logout()}>
            Log out
          </button>
        </div>
      </nav>

      <main className="main">
        <Outlet />
      </main>
    </div>
  )
}
