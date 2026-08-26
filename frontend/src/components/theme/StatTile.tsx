import type { ReactNode } from 'react'

interface StatTileProps {
  icon: ReactNode
  label: ReactNode
  value: ReactNode
  iconBg: string
  iconColor: string
}

/** Icon-chip + label/value pair, repeated identically across Dashboard and
 * DeviceDetail - consolidated here instead of duplicating the markup. */
export function StatTile({ icon, label, value, iconBg, iconColor }: StatTileProps) {
  return (
    <div className="stat-tile">
      <span className="stat-tile-icon" style={{ background: iconBg, color: iconColor }}>
        {icon}
      </span>
      <div className="stat-tile-body">
        <div className="stat-tile-label">{label}</div>
        <div className="stat-tile-value">{value}</div>
      </div>
    </div>
  )
}
