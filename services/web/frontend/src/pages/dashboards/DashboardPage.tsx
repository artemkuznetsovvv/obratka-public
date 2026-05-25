import { useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  ArrowLeft,
  Bell,
  Building2,
  CalendarRange,
  ChevronDown,
  Clock,
  Download,
  Layers,
  MapPin,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { dashboardsApi, type DashboardHeaderDto } from '@/api/dashboards'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'
import { DashboardFiltersProvider, useDashboardFilters } from './DashboardFiltersContext'
import { DashboardFilters } from './components/DashboardFilters'
import { BranchSection } from './components/BranchSection'
import { CommonMetricsLayer } from './components/CommonMetricsLayer'

const SOURCE_BADGE: Record<string, string> = {
  '2gis': 'bg-emerald-100 text-emerald-700',
  yandex: 'bg-amber-100 text-amber-700',
  google: 'bg-blue-100 text-blue-700',
}

export default function DashboardPage() {
  const { jobId } = useParams<{ jobId: string }>()
  const navigate = useNavigate()

  const dashQuery = useQuery({
    queryKey: ['dashboards', jobId],
    queryFn: () => dashboardsApi.get(jobId!),
    enabled: !!jobId,
    staleTime: 30_000,
  })

  return (
    <AppLayout
      breadcrumbs={[
        { label: 'История анализов', to: '/history' },
        { label: jobId ? `${jobId.slice(0, 8)}…` : '—', to: `/history/${jobId}` },
        { label: 'Дашборд' },
      ]}
    >
      <div className="max-w-6xl mx-auto">
        <div className="mb-6">
          <Button
            variant="outline"
            onClick={() => navigate(`/history/${jobId}`)}
            className="gap-2"
            size="sm"
          >
            <ArrowLeft size={14} />К деталям анализа
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
          <DashboardFiltersProvider header={dashQuery.data}>
            <DashboardHeader data={dashQuery.data} />
            <DashboardFilters header={dashQuery.data} />
            <DashboardBody header={dashQuery.data} />
          </DashboardFiltersProvider>
        )}
      </div>
    </AppLayout>
  )
}

// ---- Шапка дашборда ----
function DashboardHeader({ data }: { data: DashboardHeaderDto }) {
  const periodLabel = useMemo(() => {
    if (!data.periodFrom || !data.periodTo) return 'С самого начала'
    return `${formatYmd(data.periodFrom)} — ${formatYmd(data.periodTo)}`
  }, [data.periodFrom, data.periodTo])

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
          <Button variant="outline" size="sm" disabled className="gap-2" title="Будет в OBR-38">
            <Bell size={14} />
            Включить мониторинг
          </Button>
        </div>
      </div>

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

      <BranchesCollapsible branches={data.branches} />
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
        <ul className="mt-3 space-y-2">
          {branches.map((b) => (
            <li
              key={b.branchId}
              className="rounded-xl border border-border-subtle bg-card/40 px-4 py-3"
            >
              <div className="text-sm font-medium text-text-primary">
                {b.name ?? <span className="italic text-text-tertiary">Филиал удалён</span>}
              </div>
              {b.address && (
                <div className="text-xs text-text-tertiary mt-0.5 flex items-center gap-1">
                  <MapPin size={11} />
                  {b.address}
                </div>
              )}
            </li>
          ))}
        </ul>
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
  return (
    <Tabs defaultValue={branches[0].branchId} className="w-full">
      <TabsList className="h-auto flex-wrap p-1 mb-2 max-w-full">
        {branches.map((b) => {
          const isExcluded = !selectedSet.has(b.branchId)
          return (
            <TabsTrigger
              key={b.branchId}
              value={b.branchId}
              className={cn(
                'whitespace-nowrap max-w-[14rem]',
                isExcluded && 'opacity-50',
              )}
              title={isExcluded ? 'Филиал исключён фильтром «Филиал»' : undefined}
            >
              <span className="truncate">
                {b.name ?? 'Удалён'}
              </span>
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
