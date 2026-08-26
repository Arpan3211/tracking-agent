import type { ReactNode } from 'react'
import { AlertTriangleIcon, CheckCircleIcon, InfoIcon } from '../../lib/icons'

type ChipTone = 'unread' | 'acknowledged' | 'role'

interface ChipProps {
  tone?: ChipTone
  dot?: boolean
  children: ReactNode
  className?: string
}

/** Small tinted-background + saturated-text tag - never solid-fill. */
export function Chip({ tone, dot, children, className = '' }: ChipProps) {
  return (
    <span className={`chip${tone ? ` chip-${tone}` : ''}${className ? ` ${className}` : ''}`}>
      {dot && <span className="chip-dot" />}
      {children}
    </span>
  )
}

type AlertType = 'info' | 'ok' | 'success' | 'warn' | 'warning' | 'error'

const ALERT_CLASS: Record<AlertType, string> = {
  info: 'alert-info',
  ok: 'alert-ok',
  success: 'alert-ok',
  warn: 'alert-warn',
  warning: 'alert-warn',
  error: 'alert-error',
}

const ALERT_ICON: Record<AlertType, typeof InfoIcon> = {
  info: InfoIcon,
  ok: CheckCircleIcon,
  success: CheckCircleIcon,
  warn: AlertTriangleIcon,
  warning: AlertTriangleIcon,
  error: AlertTriangleIcon,
}

interface AlertProps {
  type?: AlertType
  children: ReactNode
  className?: string
}

/** Typed icon+message banner - info/ok/warn/error. */
export function Alert({ type = 'info', children, className = '' }: AlertProps) {
  const Icon = ALERT_ICON[type]
  return (
    <div className={`alert ${ALERT_CLASS[type]}${className ? ` ${className}` : ''}`}>
      <Icon size={15} />
      <span>{children}</span>
    </div>
  )
}
