import * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

const badgeVariants = cva(
  'inline-flex items-center rounded-full border px-2.5 py-0.5 text-xs font-medium transition-colors focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2',
  {
    variants: {
      variant: {
        default: 'border-transparent bg-brand text-white',
        secondary: 'border-transparent bg-state-active-bg text-brand',
        destructive: 'border-transparent bg-destructive/10 text-destructive',
        outline: 'border-border-subtle text-text-primary',
        success: 'border-transparent bg-sentiment-positive/10 text-sentiment-positive',
        warning: 'border-transparent bg-warning-amber/10 text-warning-amber',
        muted: 'border-transparent bg-muted text-text-secondary',
      },
    },
    defaultVariants: { variant: 'default' },
  },
)

export interface BadgeProps
  extends React.HTMLAttributes<HTMLDivElement>,
    VariantProps<typeof badgeVariants> {}

function Badge({ className, variant, ...props }: BadgeProps) {
  return <div className={cn(badgeVariants({ variant }), className)} {...props} />
}

export { Badge, badgeVariants }
