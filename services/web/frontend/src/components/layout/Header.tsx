import { ChevronRight } from 'lucide-react'

interface Breadcrumb {
  label: string
  to?: string
}

interface HeaderProps {
  breadcrumbs: Breadcrumb[]
}

export function Header({ breadcrumbs }: HeaderProps) {
  // Колокольчик уведомлений скрыт: функционала уведомлений пока нет.
  // Вернём, когда появится (OBR-39 Telegram / in-app).
  return (
    <header className="sticky top-0 z-10 h-header bg-card border-b border-border-subtle flex items-center px-6">
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
    </header>
  )
}
