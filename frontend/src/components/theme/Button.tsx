import type { ButtonHTMLAttributes } from 'react'

type Variant = 'primary' | 'secondary' | 'green' | 'orange' | 'danger'

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant
}

const VARIANT_CLASS: Record<Variant, string> = {
  primary: 'btn-primary',
  secondary: '',
  green: 'btn-green',
  orange: 'btn-orange',
  danger: 'btn-danger',
}

export function Button({ variant = 'secondary', className = '', ...rest }: ButtonProps) {
  const variantClass = VARIANT_CLASS[variant]
  return <button className={`btn${variantClass ? ` ${variantClass}` : ''}${className ? ` ${className}` : ''}`} {...rest} />
}
