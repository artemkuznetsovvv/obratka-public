import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Activity,
  Building2,
  Clock,
  ExternalLink,
  Loader2,
  Pause,
  Pencil,
  Play,
  RefreshCw,
  Trash2,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { cn } from '@/lib/utils'
import { describeApiError } from '@/api/errors'
import { dashboardsApi } from '@/api/dashboards'
import { useAuth } from '@/auth/AuthContext'
import {
  monitoringsApi,
  MONITORING_STATUS_LABEL,
  FREQUENCY_LABEL,
  type MonitoringListItem,
  type MonitoringStatus,
} from '@/api/monitorings'
import { MonitoringConfigDialog } from './MonitoringConfigDialog'

const STATUS_BADGE: Record<MonitoringStatus, string> = {
  active: 'bg-emerald-100 text-emerald-700',
  paused: 'bg-amber-100 text-amber-700',
  error: 'bg-red-100 text-red-700',
}

export default function MonitoringListPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const isAdmin = user?.roles.includes('Admin') ?? false

  const listQuery = useQuery({
    queryKey: ['monitorings'],
    queryFn: monitoringsApi.list,
    refetchInterval: 15_000,
  })

  const [editing, setEditing] = useState<MonitoringListItem | null>(null)

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['monitorings'] })

  const pauseM = useMutation({ mutationFn: monitoringsApi.pause, onSuccess: invalidate })
  const resumeM = useMutation({ mutationFn: monitoringsApi.resume, onSuccess: invalidate })
  const runM = useMutation({ mutationFn: monitoringsApi.run, onSuccess: invalidate })
  const removeM = useMutation({ mutationFn: monitoringsApi.remove, onSuccess: invalidate })

  const busyId =
    pauseM.isPending ? pauseM.variables
    : resumeM.isPending ? resumeM.variables
    : runM.isPending ? runM.variables
    : removeM.isPending ? removeM.variables
    : null

  return (
    <AppLayout breadcrumbs={[{ label: 'Live-мониторинг' }]}>
      <div className="max-w-5xl mx-auto">
        <div className="mb-6 flex items-center gap-2">
          <Activity size={22} className="text-brand" />
          <h1 className="text-h1 text-text-primary">Мониторинги</h1>
        </div>

        {listQuery.isLoading ? (
          <Card className="p-8 text-text-secondary">Загружаем мониторинги…</Card>
        ) : listQuery.isError ? (
          <Card className="p-8 text-destructive">
            Не удалось загрузить: {describeApiError(listQuery.error)}
          </Card>
        ) : (listQuery.data?.length ?? 0) === 0 ? (
          <Card className="p-10 text-center text-text-secondary">
            <Activity size={32} className="mx-auto mb-3 text-text-tertiary" />
            <div className="text-text-primary font-medium mb-1">Пока нет мониторингов</div>
            <p className="text-sm">
              Откройте дашборд завершённого анализа и нажмите «Включить мониторинг».
            </p>
          </Card>
        ) : (
          <div className="space-y-3">
            {listQuery.data!.map((m) => (
              <MonitoringRow
                key={m.id}
                item={m}
                busy={busyId === m.id}
                onOpen={() => navigate(`/history/${m.seedJobId}/dashboard?monitoring=${m.id}`)}
                onPause={() => pauseM.mutate(m.id)}
                onResume={() => resumeM.mutate(m.id)}
                onRun={() => runM.mutate(m.id)}
                onEdit={() => setEditing(m)}
                onDelete={() => {
                  if (
                    window.confirm(
                      `Отключить мониторинг «${m.companyName}»? Собранные отзывы и анализ останутся.`,
                    )
                  )
                    removeM.mutate(m.id)
                }}
              />
            ))}
          </div>
        )}
      </div>

      {editing && (
        <EditMonitoringDialog
          monitoring={editing}
          isAdmin={isAdmin}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            invalidate()
          }}
        />
      )}
    </AppLayout>
  )
}

function MonitoringRow({
  item,
  busy,
  onOpen,
  onPause,
  onResume,
  onRun,
  onEdit,
  onDelete,
}: {
  item: MonitoringListItem
  busy: boolean
  onOpen: () => void
  onPause: () => void
  onResume: () => void
  onRun: () => void
  onEdit: () => void
  onDelete: () => void
}) {
  const branchSummary =
    item.branches.length === 0
      ? '—'
      : item.branches
          .slice(0, 2)
          .map((b) => b.name ?? b.city ?? '—')
          .join(', ') + (item.branches.length > 2 ? ` +${item.branches.length - 2}` : '')

  return (
    <Card className="p-4">
      <div className="flex items-start justify-between gap-4 flex-wrap">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <Building2 size={16} className="text-text-tertiary shrink-0" />
            <span className="text-sm font-semibold text-text-primary truncate">{item.companyName}</span>
            <span
              className={cn(
                'inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold',
                STATUS_BADGE[item.status],
              )}
            >
              {MONITORING_STATUS_LABEL[item.status]}
            </span>
          </div>
          <div className="mt-1.5 text-xs text-text-secondary truncate" title={branchSummary}>
            {item.branches.length}{' '}
            {pluralize(item.branches.length, ['филиал', 'филиала', 'филиалов'])} · {branchSummary}
          </div>
          <div className="mt-1 flex items-center gap-3 text-xs text-text-tertiary flex-wrap">
            <span>{FREQUENCY_LABEL[item.frequency]}</span>
            <span>· окно {item.windowDays} дн.</span>
            <span className="flex items-center gap-1">
              <Clock size={11} />
              {item.lastCollectedAt
                ? `Обновлено ${formatDateTime(item.lastCollectedAt)}`
                : 'Ещё не обновлялось'}
            </span>
          </div>
        </div>

        <div className="flex items-center gap-1.5 shrink-0">
          <Button variant="outline" size="sm" className="gap-1" onClick={onOpen}>
            <ExternalLink size={13} />
            Открыть
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="gap-1"
            onClick={onRun}
            disabled={busy || item.status === 'paused'}
            title="Обновить вручную"
          >
            <RefreshCw size={13} />
          </Button>
          {item.status === 'paused' ? (
            <Button variant="outline" size="sm" className="gap-1" onClick={onResume} disabled={busy}>
              <Play size={13} />
              Возобновить
            </Button>
          ) : (
            <Button variant="outline" size="sm" className="gap-1" onClick={onPause} disabled={busy}>
              <Pause size={13} />
              Пауза
            </Button>
          )}
          <Button variant="outline" size="sm" className="gap-1" onClick={onEdit} disabled={busy}>
            <Pencil size={13} />
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="gap-1 text-destructive hover:bg-destructive/10"
            onClick={onDelete}
            disabled={busy}
          >
            {busy ? <Loader2 size={13} className="animate-spin" /> : <Trash2 size={13} />}
          </Button>
        </div>
      </div>
    </Card>
  )
}

// Изменение настроек: подтягиваем полный набор филиалов/источников из дашборда seed-job,
// инициализируем текущими значениями мониторинга.
function EditMonitoringDialog({
  monitoring,
  isAdmin,
  onClose,
  onSaved,
}: {
  monitoring: MonitoringListItem
  isAdmin: boolean
  onClose: () => void
  onSaved: () => void
}) {
  const headerQuery = useQuery({
    queryKey: ['dashboards', monitoring.seedJobId],
    queryFn: () => dashboardsApi.get(monitoring.seedJobId),
  })

  const updateM = useMutation({
    mutationFn: (vars: Parameters<typeof monitoringsApi.update>[1]) =>
      monitoringsApi.update(monitoring.id, vars),
    onSuccess: onSaved,
  })

  if (headerQuery.isLoading || !headerQuery.data) {
    // Лёгкий лоадер поверх — данные нужны, чтобы показать полный список филиалов.
    return null
  }

  const header = headerQuery.data
  return (
    <MonitoringConfigDialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Изменить настройки"
      submitLabel="Сохранить"
      isAdmin={isAdmin}
      availableSources={header.sources}
      availableBranches={header.branches.map((b) => ({
        branchId: b.branchId,
        name: b.name,
        address: b.address,
        city: b.city,
      }))}
      initial={{
        sources: monitoring.sources,
        branchIds: monitoring.branches.map((b) => b.id),
        windowDays: monitoring.windowDays,
        frequency: monitoring.frequency,
      }}
      submitting={updateM.isPending}
      errorMessage={updateM.isError ? describeApiError(updateM.error) : null}
      onSubmit={(values) => updateM.mutate(values)}
    />
  )
}

function formatDateTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString('ru-RU', {
      day: '2-digit',
      month: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    })
  } catch {
    return iso
  }
}

function pluralize(n: number, forms: [string, string, string]): string {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return forms[0]
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return forms[1]
  return forms[2]
}
