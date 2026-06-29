import { BarChart3 } from 'lucide-react'
import { cn } from '@/lib/utils'

interface LogoProps {
  withTitle?: boolean
  withTagline?: boolean
  size?: 'sm' | 'md' | 'lg'
  className?: string
}

const SIZE = {
  sm: { box: 'w-8 h-8', icon: 18, title: 'text-base' },
  md: { box: 'w-10 h-10', icon: 22, title: 'text-h3' },
  lg: { box: 'w-12 h-12', icon: 28, title: 'text-h1' },
}

export function Logo({ withTitle = true, withTagline = false, size = 'md', className }: LogoProps) {
  const dim = SIZE[size]
  return (
    <div className={cn('flex flex-col items-center gap-2', className)}>
      <div className={cn('rounded-xl bg-brand flex items-center justify-center shadow-sm', dim.box)}>
        <BarChart3 className="text-white" size={dim.icon} />
      </div>
      {withTitle && <h1 className={cn('font-bold tracking-tight text-text-primary', dim.title)}>Обратка</h1>}
      {withTagline && (
        <p className="text-caption uppercase text-text-secondary">Customer Insights Platform</p>
      )}
    </div>
  )
}
