import { NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { BellIcon, FileTextIcon, GridIcon, ListIcon, LogOutIcon, ShieldIcon, UsersIcon } from '../lib/icons'
import { Button, Chip } from '../components/theme'

export function AppLayout() {
  const { user, logout } = useAuth()
  if (!user) return null

  const isAdmin = user.role === 'admin'

  return (
    <div className="app-shell">
      <nav className="sidebar">
        <div className="sidebar-brand">
          <span className="sidebar-brand-mark">
            <ShieldIcon size={16} />
          </span>
          Employee Agent
        </div>

        <NavLink to="/" end className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
          <GridIcon size={16} /> Dashboard
        </NavLink>
        <NavLink to="/reports" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
          <FileTextIcon size={16} /> Reports
        </NavLink>
        <NavLink to="/alerts" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
          <BellIcon size={16} /> Alerts
        </NavLink>

        {isAdmin && (
          <>
            <div className="sidebar-section-label">Admin</div>
            <NavLink to="/admin/users" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
              <UsersIcon size={16} /> Users
            </NavLink>
            <NavLink to="/admin/policies" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
              <ShieldIcon size={16} /> Policies
            </NavLink>
            <NavLink to="/admin/audit-log" className={({ isActive }) => `sidebar-link${isActive ? ' active' : ''}`}>
              <ListIcon size={16} /> Audit Log
            </NavLink>
          </>
        )}

        <div className="sidebar-footer">
          <div>{user.full_name}</div>
          <Chip tone="role">{user.role}</Chip>
          <Button style={{ marginTop: 12, width: '100%' }} onClick={() => void logout()}>
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
              <LogOutIcon size={14} /> Log out
            </span>
          </Button>
        </div>
      </nav>

      <main className="main">
        <Outlet />
      </main>
    </div>
  )
}
