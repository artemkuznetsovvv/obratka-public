import type { ReactNode } from 'react'
import { Sidebar } from '@/components/layout/Sidebar'
import { Header } from '@/components/layout/Header'
import { Footer } from '@/components/layout/Footer'

interface AppLayoutProps {
  children: ReactNode
  breadcrumbs: { label: string; to?: string }[]
}

export function AppLayout({ children, breadcrumbs }: AppLayoutProps) {
  return (
    <div className="min-h-full bg-page-bg">
      <Sidebar />
      <div className="pl-sidebar flex flex-col min-h-screen">
        <Header breadcrumbs={breadcrumbs} />
        <main className="flex-1 px-10 py-8">
          <div className="mx-auto max-w-content-max">{children}</div>
        </main>
        <Footer />
      </div>
    </div>
  )
}
