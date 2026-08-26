import type { ReactNode } from 'react'

interface PageHeaderProps {
  title: ReactNode
  subtitle?: ReactNode
  actions?: ReactNode
}

/** Page shell header: bold navy title + muted subtitle - see
 * theme_spec.md section 2. */
export function PageHeader({ title, subtitle, actions }: PageHeaderProps) {
  return (
    <>
      <div className="page-header">
        <h1>{title}</h1>
        {actions}
      </div>
      {subtitle && <p className="page-subtitle">{subtitle}</p>}
    </>
  )
}
