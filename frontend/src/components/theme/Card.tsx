import type { ReactNode } from 'react'

interface CardProps {
  title?: ReactNode
  icon?: ReactNode
  accent?: boolean
  headerRight?: ReactNode
  footer?: ReactNode
  className?: string
  bodyClassName?: string
  children: ReactNode
}

/** The universal bordered container this theme uses for filters, tables,
 * forms, and detail panels alike - see theme_spec.md section 2. */
export function Card({ title, icon, accent, headerRight, footer, className = '', bodyClassName = '', children }: CardProps) {
  const hasHeader = title !== undefined || headerRight !== undefined

  return (
    <div className={`card${accent ? ' card--accent' : ''}${className ? ` ${className}` : ''}`}>
      {hasHeader && (
        <div className="card-header">
          {title !== undefined && (
            <div className="card-title" style={{ margin: 0 }}>
              {icon}
              <span>{title}</span>
            </div>
          )}
          {headerRight}
        </div>
      )}
      <div className={`card-body${bodyClassName ? ` ${bodyClassName}` : ''}`}>{children}</div>
      {footer && <div className="card-footer">{footer}</div>}
    </div>
  )
}
