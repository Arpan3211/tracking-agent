// Mirrors backend/app/schemas/*.py exactly - keep both in sync if either changes.

export type UserRole = 'employee' | 'supervisor' | 'hr' | 'admin'
export type PolicyRuleType = 'idle_threshold' | 'late_login'

export interface UserOut {
  id: string
  email: string
  full_name: string
  role: UserRole
  supervisor_id: string | null
  is_active: boolean
  created_at: string
}

export interface DeviceOut {
  id: string
  machine_name: string
  os_version: string | null
  assigned_user_id: string | null
  first_seen_at: string
  last_seen_at: string | null
}

export interface SessionOut {
  id: string
  device_id: string
  login_at: string
  logout_at: string | null
  duration_seconds: number | null
}

export interface ActivitySummaryOut {
  device_id: string
  date: string
  total_active_seconds: number
  total_idle_seconds: number
  top_apps: Record<string, number> | null
  event_count: number
}

export interface ActivityEventOut {
  id: string
  device_id: string
  event_type: string
  timestamp_utc: string
  details_raw: string | null
  details_parsed: Record<string, string> | null
  received_at: string
}

export interface AlertOut {
  id: string
  device_id: string
  policy_id: string
  triggered_at: string
  message: string
  is_read: boolean
  acknowledged_by: string | null
  acknowledged_at: string | null
}

export interface PolicyOut {
  id: string
  name: string
  rule_type: PolicyRuleType
  threshold_value: number
  created_by: string
  is_active: boolean
  created_at: string
}

export interface AuditLogOut {
  id: string
  actor_user_id: string
  action: string
  target: string | null
  timestamp: string
}

export interface Page<T> {
  items: T[]
  total: number
  page: number
  page_size: number
}
