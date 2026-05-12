import type { ReactNode } from 'react'
import { Logo } from '@/components/branding/Logo'
import { ComplianceFooter } from '@/components/branding/ComplianceFooter'

export function AuthLayout({ children }: { children: ReactNode }) {
  return (
    <div className="min-h-full bg-page-bg flex flex-col items-center justify-center p-6">
      <Logo size="lg" withTagline className="mb-8" />
      <main className="w-full max-w-[480px]">{children}</main>
      <ComplianceFooter />
    </div>
  )
}
