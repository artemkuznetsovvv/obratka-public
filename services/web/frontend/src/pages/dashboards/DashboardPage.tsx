import { useMemo, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Activity,
  ArrowLeft,
  Bell,
  Building2,
  CalendarRange,
  ChevronDown,
  Clock,
  Download,
  Layers,
  Loader2,
  MapPin,
  RefreshCw,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { dashboardsApi, type DashboardHeaderDto } from '@/api/dashboards'
import {
  monitoringsApi,
  MONITORING_STATUS_LABEL,
  FREQUENCY_LABEL,
  type MonitoringListItem,
} from '@/api/monitorings'
import { describeApiError } from '@/api/errors'
import { useAuth } from '@/auth/AuthContext'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'
import { MonitoringConfigDialog } from '@/pages/monitoring/MonitoringConfigDialog'
import { DashboardFiltersProvider, useDashboardFilters } from './DashboardFiltersContext'
import { DashboardFilters } from './components/DashboardFilters'
import { BranchSection } from './components/BranchSection'
import { CommonMetricsLayer } from './components/CommonMetricsLayer'
import { extractBranchLabel } from './branchLabel'

const SOURCE_BADGE: Record<string, string> = {
  '2gis': 'bg-emerald-100 text-emerald-700',
  yandex: 'bg-amber-100 text-amber-700',
  google: 'bg-blue-100 text-blue-700',
}

export default function DashboardPage() {
  const { jobId } = useParams<{ jobId: string }>()
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const monitoringId = searchParams.get('monitoring')

  const dashQuery = useQuery({
    queryKey: ['dashboards', jobId],
    queryFn: () => dashboardsApi.get(jobId!),
    enabled: !!jobId,
    staleTime: 30_000,
  })

  // Live-режим: открыт из раздела «Мониторинги» (?monitoring=<id>). Тянем конфиг,
  // чтобы знать окно/статус и применить окно к метрикам.
  const monitoringQuery = useQuery({
    queryKey: ['monitoring', monitoringId],
    queryFn: () => monitoringsApi.get(monitoringId!),
    enabled: !!monitoringId,
    refetchInterval: 15_000,
  })
  const monitoring = monitoringQuery.data?.monitoring ?? null

  // Окно live-режима: [now - windowDays, now] (даты, day-inclusive на бэке).
  const livePeriod = useMemo(() => {
    if (!monitoring) return { from: null as string | null, to: null as string | null }
    const to = new Date()
    const from = new Date()
    from.setDate(from.getDate() - monitoring.windowDays)
    return { from: ymd(from), to: ymd(to) }
  }, [monitoring])

  return (
    <AppLayout
      breadcrumbs={
        monitoring
          ? [
              { label: 'Live-мониторинг', to: '/monitoring' },
              { label: monitoring.companyName, to: `/monitoring/${monitoring.id}` },
              { label: 'Дашборд' },
            ]
          : [
              { label: 'История анализов', to: '/history' },
              { label: jobId ? `${jobId.slice(0, 8)}…` : '—', to: `/history/${jobId}` },
              { label: 'Дашборд' },
            ]
      }
    >
      <div className="max-w-6xl mx-auto">
        <div className="mb-6">
          <Button
            variant="outline"
            onClick={() => navigate(monitoring ? '/monitoring' : `/history/${jobId}`)}
            className="gap-2"
            size="sm"
          >
            <ArrowLeft size={14} />
            {monitoring ? 'К мониторингам' : 'К деталям анализа'}
          </Button>
        </div>

        {dashQuery.isLoading ? (
          <Card className="p-8 text-text-secondary">Загружаем дашборд…</Card>
        ) : dashQuery.isError ? (
          <Card className="p-8 text-destructive">
            Не удалось загрузить дашборд: {(dashQuery.error as Error).message}
          </Card>
        ) : !dashQuery.data ? (
          <Card className="p-8 text-text-secondary">Анализ не найден</Card>
        ) : (
          <DashboardFiltersProvider
            header={dashQuery.data}
            initialPeriodFrom={livePeriod.from}
            initialPeriodTo={livePeriod.to}
          >
            {monitoring && <LiveMonitoringBanner monitoring={monitoring} />}
            <DashboardHeader data={dashQuery.data} />
            <DashboardFilters header={dashQuery.data} />
            <DashboardBody header={dashQuery.data} />
          </DashboardFiltersProvider>
        )}
      </div>
    </AppLayout>
  )
}

// ---- Live-баннер: статус мониторинга, время обновления, ручной запуск ----
function LiveMonitoringBanner({ monitoring }: { monitoring: MonitoringListItem }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const runM = useMutation({
    mutationFn: () => monitoringsApi.run(monitoring.id),
    onSuccess: () => {
      // Цикл асинхронный — обновим статус мониторинга через пару тиков поллинга.
      queryClient.invalidateQueries({ queryKey: ['monitoring', monitoring.id] })
    },
  })

  const statusColor =
    monitoring.status === 'active'
      ? 'bg-emerald-100 text-emerald-700'
      : monitoring.status === 'paused'
        ? 'bg-amber-100 text-amber-700'
        : 'bg-red-100 text-red-700'

  return (
    <Card className="mb-4 p-4 border-brand/30 bg-state-active-bg/40">
      <div className="flex items-center justify-between gap-4 flex-wrap">
        <div className="flex items-center gap-3 flex-wrap text-sm">
          <span className="inline-flex items-center gap-1.5 font-semibold text-brand">
            <Activity size={15} />
            Live-мониторинг
          </span>
          <span className={cn('inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold', statusColor)}>
            {MONITORING_STATUS_LABEL[monitoring.status]}
          </span>
          <span className="text-text-secondary">{FREQUENCY_LABEL[monitoring.frequency]}</span>
          <span className="text-text-tertiary">· окно {monitoring.windowDays} дн.</span>
          <span className="inline-flex items-center gap-1 text-text-tertiary">
            <Clock size={12} />
            {monitoring.lastCollectedAt
              ? `Обновлено ${formatDateTime(monitoring.lastCollectedAt)}`
              : 'Ещё не обновлялось'}
          </span>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => runM.mutate()}
            disabled={runM.isPending || monitoring.status === 'paused'}
            title={monitoring.status === 'paused' ? 'Мониторинг на паузе' : 'Запустить цикл сейчас'}
          >
            {runM.isPending ? <Loader2 size={14} className="animate-spin" /> : <RefreshCw size={14} />}
            Обновить вручную
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => navigate(`/monitoring/${monitoring.id}`)}
          >
            История циклов
          </Button>
        </div>
      </div>
      {runM.isSuccess && (
        <div className="mt-2 text-xs text-text-secondary">
          Цикл запущен — новые отзывы и обновлённый дашборд появятся через несколько минут.
        </div>
      )}
    </Card>
  )
}

// ---- Шапка дашборда ----
function DashboardHeader({ data }: { data: DashboardHeaderDto }) {
  const navigate = useNavigate()
  const { user } = useAuth()
  const isAdmin = user?.roles.includes('Admin') ?? false
  const [monitorOpen, setMonitorOpen] = useState(false)

  const createMonitoring = useMutation({
    mutationFn: monitoringsApi.create,
    onSuccess: () => {
      setMonitorOpen(false)
      navigate('/monitoring')
    },
  })

  const periodLabel = useMemo(() => {
    if (!data.periodFrom || !data.periodTo) return 'С самого начала'
    return `${formatYmd(data.periodFrom)} — ${formatYmd(data.periodTo)}`
  }, [data.periodFrom, data.periodTo])

  // Мониторинг можно включать только на завершённом/частичном анализе.
  const canMonitor = data.status === 'completed' || data.status === 'partial'

  return (
    <Card className="mb-6 p-6">
      <div className="flex items-start justify-between gap-4 flex-wrap mb-4">
        <div className="min-w-0">
          <h1 className="text-h1 text-text-primary mb-2">{data.companyName}</h1>
          <div className="flex items-center gap-2 flex-wrap text-sm text-text-secondary">
            <Building2 size={14} className="text-text-tertiary" />
            <span>
              {data.branches.length}{' '}
              {pluralize(data.branches.length, ['филиал', 'филиала', 'филиалов'])}
              {/* Если филиалы в нескольких городах — указываем сколько городов */}
              {(() => {
                const cities = new Set(data.branches.map((b) => b.city).filter(Boolean))
                return cities.size > 1
                  ? ` в ${cities.size} ${pluralize(cities.size, ['городе', 'городах', 'городах'])}`
                  : null
              })()}
            </span>
            <span className="text-text-tertiary">·</span>
            <CalendarRange size={14} className="text-text-tertiary" />
            <span>{periodLabel}</span>
            <span className="text-text-tertiary">·</span>
            <Clock size={14} className="text-text-tertiary" />
            <span>Запущен {formatDateTime(data.createdAt)}</span>
          </div>
        </div>

        <div className="flex items-center gap-2 shrink-0">
          <Button variant="outline" size="sm" disabled className="gap-2" title="Будет в отдельной задаче">
            <Download size={14} />
            Скачать PDF
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            disabled={!canMonitor}
            title={canMonitor ? undefined : 'Доступно после завершения анализа'}
            onClick={() => setMonitorOpen(true)}
          >
            <Bell size={14} />
            Включить мониторинг
          </Button>
        </div>
      </div>

      <MonitoringConfigDialog
        open={monitorOpen}
        onOpenChange={setMonitorOpen}
        title="Включить мониторинг"
        submitLabel="Включить мониторинг"
        isAdmin={isAdmin}
        availableSources={data.sources}
        availableBranches={data.branches.map((b) => ({
          branchId: b.branchId,
          name: b.name,
          address: b.address,
          city: b.city,
        }))}
        submitting={createMonitoring.isPending}
        errorMessage={
          createMonitoring.isError ? describeApiError(createMonitoring.error) : null
        }
        onSubmit={(values) =>
          createMonitoring.mutate({
            companyId: data.companyId,
            seedJobId: data.jobId,
            sources: values.sources,
            branchIds: values.branchIds,
            windowDays: values.windowDays,
            frequency: values.frequency,
          })
        }
      />

      {data.sources.length > 0 && (
        <div className="mb-4 flex flex-wrap items-center gap-2">
          <span className="text-xs uppercase tracking-wide text-text-tertiary">Источники:</span>
          {data.sources.map((s) => (
            <span
              key={s}
              className={cn(
                'inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold',
                SOURCE_BADGE[s] ?? 'bg-page-bg text-text-secondary',
              )}
            >
              {SOURCE_LABEL[s] ?? s}
            </span>
          ))}
        </div>
      )}

      {/* При 1 филиале collapsible бесполезен — имя филиала и так показано
          в заголовке секции BranchSection. Показываем только при 2+. */}
      {data.branches.length > 1 && <BranchesCollapsible branches={data.branches} />}
    </Card>
  )
}

function BranchesCollapsible({
  branches,
}: {
  branches: DashboardHeaderDto['branches']
}) {
  const [expanded, setExpanded] = useState(false)
  if (branches.length === 0) {
    return (
      <div className="text-xs text-text-tertiary">
        В этом анализе не найдено филиалов с собранными отзывами.
      </div>
    )
  }
  return (
    <div>
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="inline-flex items-center gap-1.5 text-xs text-text-secondary hover:text-text-primary transition-colors"
        aria-expanded={expanded}
      >
        <Layers size={12} />
        Список филиалов
        <ChevronDown
          size={14}
          className={cn('transition-transform', expanded && 'rotate-180')}
        />
      </button>
      {expanded && (
        <div className="mt-3">
          <GroupedBranchList branches={branches} />
        </div>
      )}
    </div>
  )
}

// ---- Тело дашборда: layout 1/2/3+ + слой общих + секции филиалов ----
// Решение по итерации 2: layout (видимость слоя общих и табов) определяется
// branches джоба (статично), фильтр «филиал» влияет только на цифры внутри
// карточек. См. /loop из истории — это компромисс между букв. чтением спеки
// («по выбранным филиалам») и стабильным UX.
function DashboardBody({ header }: { header: DashboardHeaderDto }) {
  const branches = header.branches
  const branchCount = branches.length

  if (branchCount === 0) {
    return (
      <Card className="p-8 text-center text-text-secondary">
        В этом анализе не найдено филиалов с собранными отзывами. Возможно,
        сбор провалился — проверь страницу анализа.
      </Card>
    )
  }

  // 1 филиал: одна секция, без табов и слоя общих
  if (branchCount === 1) {
    return <BranchSection branch={branches[0]} />
  }

  // 2 филиала: табы (по решению из вопроса юзера — единый паттерн с 3+)
  // 3+ филиалов: табы + слой общих метрик сверху
  return (
    <>
      {branchCount >= 3 && <CommonMetricsLayer />}
      <BranchTabs branches={branches} />
    </>
  )
}

function BranchTabs({ branches }: { branches: DashboardHeaderDto['branches'] }) {
  const filters = useDashboardFilters()
  const selectedSet = new Set(filters.branches)
  const uniqueCities = new Set(branches.map((b) => b.city).filter(Boolean))
  const isMultiCity = uniqueCities.size > 1
  return (
    <Tabs defaultValue={branches[0].branchId} className="w-full">
      <TabsList className="h-auto flex-wrap p-1 mb-2 max-w-full">
        {branches.map((b) => {
          const isExcluded = !selectedSet.has(b.branchId)
          const tabLabel = extractBranchLabel(b.address, b.name, {
            cityHint: isMultiCity ? b.city : null,
          })
          // Tooltip всегда содержит full name+address для контекста (имя
          // может быть полезно, особенно когда из адреса торчит только
          // улица — «Skuratov на Пушкина, 5»).
          const tooltipParts = [b.name, b.address].filter(Boolean).join(' · ')
          return (
            <TabsTrigger
              key={b.branchId}
              value={b.branchId}
              className={cn(
                'whitespace-nowrap max-w-[14rem]',
                isExcluded && 'opacity-50',
              )}
              title={
                isExcluded
                  ? `Филиал исключён фильтром «Филиал». ${tooltipParts}`
                  : tooltipParts || undefined
              }
            >
              <span className="truncate">{tabLabel ?? 'Удалён'}</span>
            </TabsTrigger>
          )
        })}
      </TabsList>
      {branches.map((b) => (
        <TabsContent key={b.branchId} value={b.branchId} className="mt-4">
          <BranchSection branch={b} />
        </TabsContent>
      ))}
    </Tabs>
  )
}

// extractBranchLabel вынесена в ./branchLabel.ts — переиспользуется в табах
// (здесь) и в опциях фильтра «Филиал» (DashboardFilters).

// Список филиалов в шапке. При multi-city группируем по городам с заголовком,
// при одном городе — плоский список (без лишней секции).
function GroupedBranchList({ branches }: { branches: DashboardHeaderDto['branches'] }) {
  const groups = new Map<string, DashboardHeaderDto['branches']>()
  for (const b of branches) {
    const key = b.city ?? '— без города —'
    const arr = groups.get(key) ?? []
    arr.push(b)
    groups.set(key, arr)
  }
  const isMultiCity = groups.size > 1
  return (
    <div className="space-y-3">
      {Array.from(groups.entries()).map(([city, items]) => (
        <div key={city}>
          {isMultiCity && (
            <div className="text-xs uppercase tracking-wide text-text-tertiary mb-1.5 px-1">
              {city} · {items.length}
            </div>
          )}
          <ul className="space-y-2">
            {items.map((b) => (
              <li
                key={b.branchId}
                className="rounded-xl border border-border-subtle bg-card/40 px-4 py-3"
              >
                <div className="text-sm font-medium text-text-primary flex items-center gap-1">
                  <MapPin size={12} className="text-text-tertiary shrink-0" />
                  <span className="truncate">
                    {b.address ?? (
                      b.name ?? <span className="italic text-text-tertiary">Филиал удалён</span>
                    )}
                  </span>
                </div>
                {b.address && b.name && (
                  <div className="text-xs text-text-tertiary mt-0.5 truncate">{b.name}</div>
                )}
              </li>
            ))}
          </ul>
        </div>
      ))}
    </div>
  )
}

// ---- helpers ----
function formatDateTime(iso: string): string {
  try {
    const d = new Date(iso)
    return d.toLocaleString('ru-RU', {
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    })
  } catch {
    return iso
  }
}

function ymd(d: Date): string {
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}

function formatYmd(iso: string): string {
  const ymd = iso.slice(0, 10)
  const parts = ymd.split('-')
  if (parts.length !== 3) return iso
  return `${parts[2]}.${parts[1]}.${parts[0]}`
}

function pluralize(n: number, forms: [string, string, string]): string {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return forms[0]
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return forms[1]
  return forms[2]
}
