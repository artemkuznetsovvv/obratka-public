import { NavLink, useLocation, useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Activity, BarChart3, Bell, Building2, Clock, Globe, Inbox, ListTodo, LogOut, Plus, Shield, User, Users, type LucideIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Logo } from '@/components/branding/Logo'
import { useAuth } from '@/auth/AuthContext'
import { adminUserRequestsApi } from '@/api/admin'
import { cn } from '@/lib/utils'

interface NavItem {
  to: string
  label: string
  icon: LucideIcon
  disabled?: boolean
  badge?: number
}

const userNav: NavItem[] = [
  { to: '/monitoring', label: 'Live-мониторинг', icon: Activity },
  { to: '/history', label: 'История анализов', icon: Clock },
]

const adminNav: NavItem[] = [
  { to: '/admin/users', label: 'Пользователи', icon: Users },
  { to: '/admin/requests', label: 'Запросы', icon: Inbox },
  { to: '/admin/companies', label: 'Компании', icon: Building2 },
  { to: '/admin/proxies', label: 'Прокси', icon: Globe },
  { to: '/admin/parser-tasks', label: 'Парсер-задачи', icon: ListTodo },
  { to: '/admin/analyses', label: 'Анализы', icon: BarChart3 },
]

export function Sidebar() {
  const { user, logout } = useAuth()
  const isAdmin = user?.roles.includes('Admin') ?? false
  const navigate = useNavigate()
  const location = useLocation()

  // Бейдж непрочитанных запросов пользователей (сброс пароля и т.п.) на пункте «Запросы».
  const requestsQuery = useQuery({
    queryKey: ['admin', 'user-requests', 'new-count'],
    queryFn: () => adminUserRequestsApi.list('new'),
    enabled: isAdmin,
    refetchInterval: 60_000,
  })
  const newRequestsCount = requestsQuery.data?.newCount ?? 0

  return (
    <aside className="fixed inset-y-0 left-0 w-sidebar flex flex-col bg-card border-r border-border-subtle">
      <div className="p-6 flex items-center gap-2">
        <Logo size="sm" withTitle={false} />
        <div>
          <div className="font-bold text-text-primary leading-none">Обратка</div>
          <div className="text-caption uppercase text-text-secondary mt-1">Customer Insights</div>
        </div>
      </div>

      <nav className="flex-1 px-3 py-4 space-y-1 overflow-y-auto">
        {userNav.map((item) => (
          <SidebarLink key={item.to} {...item} />
        ))}

        {isAdmin && (
          <>
            <div className="my-3 border-t border-border-subtle" />
            <div className="px-3 py-2 flex items-center gap-2 text-caption uppercase text-text-tertiary">
              <Shield size={12} />
              <span>Админ</span>
            </div>
            {adminNav.map((item) => (
              <SidebarLink
                key={item.to}
                {...item}
                badge={item.to === '/admin/requests' ? newRequestsCount : undefined}
              />
            ))}
          </>
        )}
      </nav>

      <div className="p-3 border-t border-border-subtle space-y-1">
        <Button
          variant="default"
          size="default"
          className="w-full justify-center gap-2"
          onClick={() => navigate('/analyses/new')}
        >
          <Plus size={16} />
          Новый анализ
        </Button>
        <button
          onClick={() => navigate('/notifications')}
          className={cn(
            'w-full flex items-center gap-3 px-3 py-2 rounded-md text-sm transition-colors',
            location.pathname === '/notifications'
              ? 'bg-state-active-bg text-brand font-medium'
              : 'text-text-secondary hover:bg-page-bg hover:text-text-primary',
          )}
        >
          <Bell size={16} />
          <span>Уведомления</span>
        </button>
        <button
          onClick={() => navigate('/profile')}
          className={cn(
            'w-full flex items-center gap-3 px-3 py-2 rounded-md text-sm transition-colors',
            location.pathname === '/profile'
              ? 'bg-state-active-bg text-brand font-medium'
              : 'text-text-secondary hover:bg-page-bg hover:text-text-primary',
          )}
        >
          <User size={16} />
          <span>Профиль</span>
        </button>
        <button
          onClick={logout}
          className="w-full flex items-center gap-3 px-3 py-2 rounded-md text-sm text-destructive hover:bg-destructive/10 transition-colors"
        >
          <LogOut size={16} />
          <span>Выйти</span>
        </button>
      </div>
    </aside>
  )
}

function SidebarLink({ to, label, icon: Icon, disabled, badge }: NavItem) {
  const location = useLocation()
  if (disabled) {
    return (
      <div
        className="flex items-center gap-3 px-3 py-2 rounded-md text-sm text-text-tertiary cursor-not-allowed select-none"
        title="Скоро будет"
      >
        <Icon size={16} />
        <span>{label}</span>
      </div>
    )
  }
  const isActive = location.pathname.startsWith(to)
  return (
    <NavLink
      to={to}
      className={cn(
        'relative flex items-center gap-3 px-3 py-2 rounded-md text-sm transition-colors',
        isActive
          ? 'bg-state-active-bg text-brand font-medium'
          : 'text-text-secondary hover:bg-page-bg hover:text-text-primary',
      )}
    >
      {isActive && <span className="absolute left-0 top-1.5 bottom-1.5 w-[3px] rounded-r bg-brand" />}
      <Icon size={16} />
      <span>{label}</span>
      {!!badge && badge > 0 && (
        <span className="ml-auto inline-flex min-w-[18px] h-[18px] items-center justify-center rounded-full bg-brand px-1 text-[11px] font-semibold text-white">
          {badge}
        </span>
      )}
    </NavLink>
  )
}
