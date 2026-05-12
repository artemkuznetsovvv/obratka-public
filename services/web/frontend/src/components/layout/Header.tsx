import { Bell, ChevronRight } from 'lucide-react'

interface Breadcrumb {
  label: string
  to?: string
}

interface HeaderProps {
  breadcrumbs: Breadcrumb[]
}

export function Header({ breadcrumbs }: HeaderProps) {
  return (
    <header className="sticky top-0 z-10 h-header bg-card border-b border-border-subtle flex items-center justify-between px-6">
      <nav className="flex items-center gap-2 text-sm">
        {breadcrumbs.map((bc, idx) => (
          <span key={idx} className="flex items-center gap-2 text-text-secondary">
            {idx > 0 && <ChevronRight size={14} className="text-text-tertiary" />}
            <span className={idx === breadcrumbs.length - 1 ? 'text-text-primary font-medium' : ''}>
              {bc.label}
            </span>
          </span>
        ))}
      </nav>
      <button
        className="p-2 rounded-md hover:bg-page-bg text-text-secondary"
        aria-label="Уведомления"
      >
        <Bell size={18} />
      </button>
    </header>
  )
}
