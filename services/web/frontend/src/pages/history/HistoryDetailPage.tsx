import { useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  AlertCircle,
  ArrowLeft,
  ArrowRight,
  Building2,
  CalendarRange,
  Check,
  CheckCircle2,
  ChevronDown,
  Clock,
  Layers,
  LayoutDashboard,
  ListTree,
  Loader2,
  MapPin,
  RefreshCcw,
  Sparkles,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import {
  analysesApi,
  type AnalysisJob,
  type BranchStatsDto,
  type CollectionProgressEntry,
} from '@/api/analyses'
import { companiesApi, type LogicalBranchDto } from '@/api/companies'
import { cn } from '@/lib/utils'
import {
  approximateProgress,
  buildDetailStepper,
  buildPipelineStages,
  isTerminal,
  SOURCE_LABEL,
  statusMetaFor,
  type PipelineStage,
  type StepperStep,
} from './analysisStatus'
import { RecommendationsBlock } from './RecommendationsBlock'

const SOURCE_BADGE: Record<string, string> = {
  '2gis': 'bg-emerald-100 text-emerald-700',
  yandex: 'bg-amber-100 text-amber-700',
  google: 'bg-blue-100 text-blue-700',
}

export default function HistoryDetailPage() {
  const { jobId } = useParams<{ jobId: string }>()
  const navigate = useNavigate()

  const jobQuery = useQuery({
    queryKey: ['analyses', jobId],
    queryFn: () => analysesApi.get(jobId!),
    enabled: !!jobId,
    refetchInterval: (q) => {
      const status = q.state.data?.status
      if (!status) return 3000
      return isTerminal(status) ? false : 3000
    },
    refetchIntervalInBackground: false,
  })

  const companyQuery = useQuery({
    queryKey: ['company', jobQuery.data?.companyId],
    queryFn: () => companiesApi.get(jobQuery.data!.companyId),
    enabled: !!jobQuery.data?.companyId,
    staleTime: 60_000,
  })

  const job = jobQuery.data
  const meta = useMemo(() => (job ? statusMetaFor(job.status) : null), [job])
  const overallProgress = useMemo(
    () => (job ? approximateProgress(job.status, job.collectionProgress) : 0),
    [job],
  )
  const stages = useMemo(() => (job ? buildPipelineStages(job.status) : []), [job])
  const stepper = useMemo(() => (job ? buildDetailStepper(job.status) : []), [job])

  const sourceEntries = useMemo<Array<[string, CollectionProgressEntry]>>(
    () => (job ? Object.entries(job.collectionProgress) : []),
    [job],
  )

  const periodLabel = useMemo(() => {
    if (!companyQuery.data) return ''
    const from = companyQuery.data.draftPeriodFrom
    const to = companyQuery.data.draftPeriodTo
    if (!from || !to) return 'С самого начала'
    return `${formatYmd(from)} — ${formatYmd(to)}`
  }, [companyQuery.data])

  return (
    <AppLayout
      breadcrumbs={[
        { label: 'История анализов', to: '/history' },
        { label: jobId ? `${jobId.slice(0, 8)}…` : '—' },
      ]}
    >
      <div className="max-w-4xl mx-auto">
        <div className="mb-6 flex items-center justify-between gap-3">
          <Button
            variant="outline"
            onClick={() => navigate('/history')}
            className="gap-2"
            size="sm"
          >
            <ArrowLeft size={14} />
            К истории
          </Button>
          {jobQuery.isFetching && !jobQuery.isLoading && (
            <span className="inline-flex items-center gap-1.5 text-xs text-text-tertiary">
              <RefreshCcw size={12} className="animate-spin [animation-duration:1.5s]" />
              Обновляем…
            </span>
          )}
        </div>

        {jobQuery.isLoading ? (
          <Card className="p-8 text-text-secondary">Загружаем данные анализа…</Card>
        ) : jobQuery.isError ? (
          <Card className="p-8 text-destructive">
            Не удалось загрузить анализ: {(jobQuery.error as Error).message}
          </Card>
        ) : !job || !meta ? (
          <Card className="p-8 text-text-secondary">Анализ не найден</Card>
        ) : (
          <>
            {/* Header */}
            <div className="mb-8">
              <div className="flex items-center gap-2 mb-2 flex-wrap">
                <span
                  className={cn(
                    'inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold border',
                    meta.badge,
                  )}
                >
                  {meta.label}
                </span>
              </div>
              <h1 className="text-h1 text-text-primary mb-2">
                {job.status === 'completed' || job.status === 'partial'
                  ? 'Анализ готов'
                  : job.status === 'failed'
                  ? 'Анализ не завершился'
                  : 'Идёт анализ'}
              </h1>
              <div className="flex items-center gap-2 flex-wrap text-sm text-text-secondary">
                <Building2 size={14} className="text-text-tertiary" />
                <span>{companyQuery.data?.name ?? '…'}</span>
                {periodLabel && (
                  <>
                    <span className="text-text-tertiary">·</span>
                    <CalendarRange size={14} className="text-text-tertiary" />
                    <span>{periodLabel}</span>
                  </>
                )}
                <span className="text-text-tertiary">·</span>
                <Clock size={14} className="text-text-tertiary" />
                <span>Запущен {formatDateTime(job.createdAt)}</span>
              </div>
            </div>

            {/* Stepper 1→4 */}
            <DetailStepper steps={stepper} />

            {/* Central progress card */}
            <Card className="p-8 sm:p-12 mb-6">
              <div className="flex flex-col md:flex-row items-center md:items-start gap-8 md:gap-16">
                <CircularProgress value={overallProgress} status={job.status} />
                <div className="flex-1 w-full">
                  <ul className="space-y-3">
                    {stages.map((s) => (
                      <StageItem key={s.key} stage={s} />
                    ))}
                  </ul>
                  <ProcessingHint job={job} />
                </div>
              </div>
            </Card>

            {/* Per-source breakdown */}
            {sourceEntries.length > 0 && (
              <Card className="p-6 mb-6">
                <div className="text-h3 text-text-primary mb-4 flex items-center gap-2">
                  <CalendarRange size={16} className="text-text-tertiary" />
                  Прогресс по источникам
                </div>
                <div className="space-y-4">
                  {sourceEntries.map(([source, entry]) => (
                    <SourceProgressRow key={source} source={source} entry={entry} />
                  ))}
                </div>
                <p className="mt-4 text-[11px] text-text-tertiary">
                  Прогресс по конкретным филиалам внутри источника пока недоступен —
                  Parser отдаёт значение на уровне всего источника. Запланировано как
                  улучшение парсера.
                </p>
              </Card>
            )}

            {/* Collapsible: параметры анализа (текущая группировка компании) */}
            <AnalysisParamsCard companyId={job.companyId} />

            {/* Result */}
            {(job.status === 'completed' || job.status === 'partial') && (
              <Card className="p-6 mt-6">
                <div className="text-h3 text-text-primary mb-3 flex items-center gap-2">
                  <CheckCircle2 size={16} className="text-emerald-600" />
                  Результаты
                </div>
                <div className="grid grid-cols-2 sm:grid-cols-3 gap-3 mb-4">
                  <StatCard
                    label="Отзывов"
                    value={job.reviewCount.toLocaleString('ru-RU')}
                  />
                  <StatCard
                    label="Рекомендаций"
                    value={job.recommendationsCount.toLocaleString('ru-RU')}
                  />
                  <StatCard label="Статус" value={meta.label} />
                </div>
                {job.summary ? (
                  <div className="rounded-xl border border-border-subtle bg-page-bg/40 p-4 mb-4">
                    <div className="text-xs uppercase tracking-wide text-text-tertiary mb-1.5 flex items-center gap-1">
                      <Sparkles size={12} />
                      Резюме от AI
                    </div>
                    <div className="text-sm text-text-primary whitespace-pre-line">{job.summary}</div>
                  </div>
                ) : (
                  <div className="text-sm text-text-tertiary mb-4">
                    Сводка LLM ещё не получена. Если статус «Частично» — часть данных не собралась,
                    но финальный отчёт может всё равно сформироваться.
                  </div>
                )}

                <div className="mb-4">
                  <RecommendationsBlock jobId={job.id} />
                </div>

                <BranchStatsBlock jobId={job.id} companyId={job.companyId} />

                <div className="mt-5 flex items-center justify-end gap-3 flex-wrap">
                  <Button
                    size="sm"
                    onClick={() => navigate(`/history/${job.id}/dashboard`)}
                    className="gap-2"
                  >
                    <LayoutDashboard size={14} />
                    Открыть дашборд
                    <ArrowRight size={14} />
                  </Button>
                </div>
              </Card>
            )}

            {/* Failed banner — отдельной карточкой */}
            {job.error && (
              <Card className="p-5 border-rose-200 bg-rose-50">
                <div className="flex items-start gap-3">
                  <AlertCircle size={18} className="text-rose-700 shrink-0 mt-0.5" />
                  <div className="text-sm text-rose-900">
                    <div className="font-medium mb-0.5">Ошибка</div>
                    <div className="text-rose-800 whitespace-pre-line">{job.error}</div>
                  </div>
                </div>
              </Card>
            )}
          </>
        )}
      </div>
    </AppLayout>
  )
}

// ----- 4-step stepper -----

function DetailStepper({ steps }: { steps: StepperStep[] }) {
  return (
    <div className="mb-8 max-w-2xl mx-auto">
      <div className="flex items-center justify-between relative">
        {/* connector line */}
        <div className="absolute top-1/2 left-0 w-full h-[2px] bg-page-bg -translate-y-1/2 -z-10" />
        {/* filled portion — by fraction of done/active steps */}
        <div
          className="absolute top-1/2 left-0 h-[2px] bg-brand/70 -translate-y-1/2 -z-10 transition-all"
          style={{ width: `${connectorPct(steps)}%` }}
        />
        {steps.map((s) => (
          <div
            key={s.index}
            className="flex flex-col items-center gap-2 bg-page-bg px-3"
          >
            <StepCircle step={s} />
            <span
              className={cn(
                'text-xs font-medium',
                s.state === 'active'
                  ? 'text-brand'
                  : s.state === 'done'
                  ? 'text-text-primary'
                  : s.state === 'failed'
                  ? 'text-rose-600'
                  : 'text-text-tertiary',
              )}
            >
              {s.label}
            </span>
          </div>
        ))}
      </div>
    </div>
  )
}

function StepCircle({ step }: { step: StepperStep }) {
  const base = 'w-9 h-9 rounded-full flex items-center justify-center text-sm font-bold'
  if (step.state === 'done') {
    return (
      <div className={cn(base, 'bg-brand text-white')}>
        <Check size={16} />
      </div>
    )
  }
  if (step.state === 'active') {
    return (
      <div className={cn(base, 'bg-card border-[3px] border-brand text-brand')}>
        {step.index}
      </div>
    )
  }
  if (step.state === 'failed') {
    return (
      <div className={cn(base, 'bg-rose-100 text-rose-700')}>
        <AlertCircle size={16} />
      </div>
    )
  }
  return (
    <div className={cn(base, 'bg-card border-2 border-border-subtle text-text-tertiary')}>
      {step.index}
    </div>
  )
}

// Сколько связи между шагами «закрашено»: done = 1, active = 0.5, остальное = 0.
// Делим на (steps.length - 1), переводим в проценты.
function connectorPct(steps: StepperStep[]): number {
  if (steps.length <= 1) return 0
  let filled = 0
  for (const s of steps) {
    if (s.state === 'done') filled += 1
    else if (s.state === 'active' || s.state === 'failed') filled += 0.5
  }
  // -1 потому что коннектор только между шагами, не до последнего.
  const pct = ((filled - 0.5) / (steps.length - 1)) * 100
  return Math.max(0, Math.min(100, pct))
}

// ----- Circular progress -----

function CircularProgress({ value, status }: { value: number; status: string }) {
  // conic-gradient как в design _4 + maskcl для тонкого кольца через radial-gradient.
  const safeValue = Math.max(0, Math.min(100, value))
  const isFailed = status === 'failed'
  const ringColor = isFailed
    ? '#f43f5e'
    : status === 'completed' || status === 'partial'
    ? '#10b981'
    : '#4f46e5'
  return (
    <div
      className="relative w-44 h-44 rounded-full flex items-center justify-center shrink-0"
      style={{
        background: `radial-gradient(closest-side, white 79%, transparent 80% 100%), conic-gradient(${ringColor} ${safeValue}%, #EEF2FF 0)`,
      }}
    >
      <div className="text-center">
        <div className="text-[40px] font-bold leading-none text-text-primary">
          {Math.round(safeValue)}%
        </div>
        <div className="text-[11px] uppercase tracking-widest text-text-tertiary mt-1.5">
          {isFailed ? 'Ошибка' : status === 'completed' || status === 'partial' ? 'Готово' : 'Идёт'}
        </div>
      </div>
    </div>
  )
}

// ----- Stage list item -----

function StageItem({ stage }: { stage: PipelineStage }) {
  return (
    <li className="flex items-center gap-3">
      <StageIcon state={stage.state} />
      <span
        className={cn(
          'text-sm',
          stage.state === 'active'
            ? 'font-semibold text-brand'
            : stage.state === 'done'
            ? 'text-text-secondary'
            : stage.state === 'failed'
            ? 'text-rose-700'
            : 'text-text-tertiary',
        )}
      >
        {stage.label}
      </span>
    </li>
  )
}

function StageIcon({ state }: { state: PipelineStage['state'] }) {
  if (state === 'done') {
    return (
      <div className="w-5 h-5 rounded-full bg-emerald-100 text-emerald-600 flex items-center justify-center">
        <Check size={12} strokeWidth={3} />
      </div>
    )
  }
  if (state === 'active') {
    return (
      <div className="w-5 h-5 flex items-center justify-center">
        <Loader2 size={16} className="text-brand animate-spin" />
      </div>
    )
  }
  if (state === 'failed') {
    return (
      <div className="w-5 h-5 rounded-full bg-rose-100 text-rose-600 flex items-center justify-center">
        <AlertCircle size={12} />
      </div>
    )
  }
  // pending
  return (
    <div className="w-5 h-5 flex items-center justify-center">
      <span className="w-2 h-2 rounded-full bg-border-subtle" />
    </div>
  )
}

// ----- Processing hint (под списком стадий) -----

function ProcessingHint({ job }: { job: AnalysisJob }) {
  // На стадии collecting — какой источник сейчас "running" + сколько отзывов уже собрано.
  // На остальных стадиях — total отзывов.
  let text: React.ReactNode = null
  if (job.status === 'collecting') {
    const entries = Object.entries(job.collectionProgress)
    const running = entries.find(([, e]) => e.status === 'running')
    const totalCollected = entries.reduce(
      (acc, [, e]) => acc + (e.reviewCount ?? 0),
      0,
    )
    if (running) {
      const label = SOURCE_LABEL[running[0]] ?? running[0]
      text = (
        <>
          Собираем <span className="font-semibold text-text-primary">{label}</span>
          {totalCollected > 0 && (
            <>
              {' · '}
              <span className="font-semibold text-text-primary">
                {totalCollected.toLocaleString('ru-RU')}
              </span>{' '}
              отзывов собрано
            </>
          )}
        </>
      )
    } else if (totalCollected > 0) {
      text = (
        <>
          Собрано{' '}
          <span className="font-semibold text-text-primary">
            {totalCollected.toLocaleString('ru-RU')}
          </span>{' '}
          отзывов
        </>
      )
    } else {
      text = 'Запускаем сбор отзывов…'
    }
  } else if (
    job.status === 'sent_to_llm' ||
    job.status === 'computing_aggregates'
  ) {
    text = (
      <>
        Обрабатываем{' '}
        <span className="font-semibold text-text-primary">
          {job.reviewCount.toLocaleString('ru-RU')}
        </span>{' '}
        отзывов
      </>
    )
  } else if (job.status === 'completed' || job.status === 'partial') {
    text = (
      <>
        Всего обработано{' '}
        <span className="font-semibold text-text-primary">
          {job.reviewCount.toLocaleString('ru-RU')}
        </span>{' '}
        отзывов
      </>
    )
  }

  if (!text) return null
  return (
    <div className="mt-6 pt-5 border-t border-border-subtle flex items-center gap-2 text-sm text-text-secondary">
      <RefreshCcw
        size={14}
        className={cn(
          'text-text-tertiary shrink-0',
          !isTerminal(job.status) && 'animate-spin [animation-duration:2s]',
        )}
      />
      <span>{text}</span>
    </div>
  )
}

// ----- Per-source row -----

function SourceProgressRow({
  source,
  entry,
}: {
  source: string
  entry: CollectionProgressEntry
}) {
  const sourceLabel = SOURCE_LABEL[source] ?? source
  const badgeColor = SOURCE_BADGE[source] ?? 'bg-page-bg text-text-secondary'
  const entryMeta = statusMetaFor(mapTaskStatusToBadge(entry.status))
  // PG отдаёт progress per source как int 0..100 — берём как есть. completed/failed
  // ставим в 100 чтобы бар был полным (failed красным).
  const pct =
    entry.status === 'completed' || entry.status === 'failed'
      ? 100
      : Math.round(entry.progress ?? 0)
  return (
    <div>
      <div className="flex items-center justify-between gap-2 mb-1">
        <div className="flex items-center gap-2 min-w-0">
          <span
            className={cn(
              'inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold shrink-0',
              badgeColor,
            )}
          >
            {sourceLabel}
          </span>
          <span
            className={cn(
              'inline-flex items-center px-2 py-0.5 rounded-full text-[10px] font-semibold border',
              entryMeta.badge,
            )}
          >
            {entryMeta.label}
          </span>
        </div>
        <span className="text-xs text-text-tertiary">
          {entry.reviewCount !== null && entry.reviewCount !== undefined
            ? `${entry.reviewCount.toLocaleString('ru-RU')} отзывов`
            : entry.status === 'failed'
            ? '—'
            : entry.status === 'pending'
            ? 'ожидание'
            : `${pct}%`}
        </span>
      </div>
      <ProgressBar value={pct} tone={entryMeta.tone} />
      {entry.error && (
        <div className="mt-1 text-xs text-rose-700 truncate" title={entry.error}>
          {entry.error}
        </div>
      )}
    </div>
  )
}

function mapTaskStatusToBadge(taskStatus: string): string {
  switch (taskStatus) {
    case 'pending':
      return 'pending'
    case 'running':
      return 'collecting'
    case 'completed':
      return 'completed'
    case 'failed':
      return 'failed'
    default:
      return taskStatus
  }
}

function StatCard({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-border-subtle bg-card px-4 py-3">
      <div className="text-xs text-text-tertiary uppercase tracking-wide mb-1">{label}</div>
      <div className="text-lg font-semibold text-text-primary">{value}</div>
    </div>
  )
}

function ProgressBar({
  value,
  tone,
}: {
  value: number
  tone: 'pending' | 'progress' | 'ok' | 'warn' | 'error'
}) {
  const color =
    tone === 'ok'
      ? 'bg-emerald-500'
      : tone === 'warn'
      ? 'bg-amber-500'
      : tone === 'error'
      ? 'bg-rose-500'
      : tone === 'progress'
      ? 'bg-brand'
      : 'bg-slate-400'
  return (
    <div className="h-2 w-full rounded-full bg-page-bg overflow-hidden">
      <div
        className={cn('h-full transition-all', color)}
        style={{ width: `${Math.max(2, Math.min(100, value))}%` }}
      />
    </div>
  )
}

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
  // iso мог быть с time/tz; берём первые 10 символов и переворачиваем yyyy-mm-dd → dd.mm.yyyy.
  const ymd = iso.slice(0, 10)
  const parts = ymd.split('-')
  if (parts.length !== 3) return iso
  return `${parts[2]}.${parts[1]}.${parts[0]}`
}

// ----- Collapsible: параметры анализа (выбранные филиалы) -----
//
// Читает companiesApi.listGroups(companyId) — это ТЕКУЩЕЕ состояние группировки
// в Company, не snapshot. Если юзер успел перегруппировать после запуска — здесь
// покажет новую группировку (см. processing-gateway-todo.md, пункт «snapshot
// выбранных филиалов per-job»). Для MVP допустимо.
function AnalysisParamsCard({ companyId }: { companyId: string }) {
  const [expanded, setExpanded] = useState(false)
  // Lazy load — query запускается только при первом раскрытии. После закрытия
  // данные остаются в кеше TanStack, повторное открытие — мгновенное.
  const groupsQuery = useQuery({
    queryKey: ['company', companyId, 'groups'],
    queryFn: () => companiesApi.listGroups(companyId),
    enabled: expanded,
    staleTime: 60_000,
  })

  const activeGroups = useMemo(
    () =>
      (groupsQuery.data ?? []).filter(
        (g) => g.isSelected && g.providers.some((p) => p.isEnabled),
      ),
    [groupsQuery.data],
  )

  return (
    <Card className="overflow-hidden">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="w-full flex items-center justify-between gap-3 px-6 py-4 text-left hover:bg-page-bg/40 transition-colors"
        aria-expanded={expanded}
      >
        <div className="flex items-center gap-2 text-h3 text-text-primary">
          <ListTree size={16} className="text-text-tertiary" />
          Параметры анализа
        </div>
        <ChevronDown
          size={18}
          className={cn(
            'text-text-tertiary transition-transform',
            expanded && 'rotate-180',
          )}
        />
      </button>

      {expanded && (
        <div className="border-t border-border-subtle px-6 py-5">
          {groupsQuery.isLoading ? (
            <div className="text-sm text-text-tertiary">Загружаем группировку…</div>
          ) : groupsQuery.isError ? (
            <div className="text-sm text-destructive">
              Не удалось загрузить: {(groupsQuery.error as Error).message}
            </div>
          ) : activeGroups.length === 0 ? (
            <div className="text-sm text-text-tertiary">
              Группировка не найдена — возможно, компания была перегруппирована или удалена.
            </div>
          ) : (
            <>
              <div className="mb-3 text-xs text-text-tertiary">
                Текущая группировка компании ({activeGroups.length}{' '}
                {pluralize(activeGroups.length, ['филиал', 'филиала', 'филиалов'])}).
                Если меняли мастер после запуска — здесь видно последнее состояние,
                а не то, с чем реально гонялся анализ.
              </div>
              <ul className="space-y-3">
                {activeGroups.map((g) => (
                  <ParamsBranchRow key={g.id} group={g} />
                ))}
              </ul>
            </>
          )}
        </div>
      )}
    </Card>
  )
}

function ParamsBranchRow({ group }: { group: LogicalBranchDto }) {
  const activeProviders = group.providers.filter((p) => p.isEnabled)
  return (
    <li className="rounded-xl border border-border-subtle bg-card/40 px-4 py-3">
      {/* Иерархия перевёрнута: адрес сверху крупно, имя мельче под ним.
          У сетевых брендов имя одинаковое у всех филиалов — различить
          можно только адресом (тот же приём что в BranchSearchPage). */}
      <div className="text-sm font-medium text-text-primary flex items-center gap-1">
        <MapPin size={12} className="text-text-tertiary shrink-0" />
        <span className="truncate">
          {group.address || <span className="italic text-text-tertiary">Адрес не указан</span>}
        </span>
      </div>
      {group.name && (
        <div className="text-xs text-text-tertiary mt-0.5 truncate">{group.name}</div>
      )}
      <div className="mt-2 flex flex-wrap gap-1.5">
        {activeProviders.map((p) => {
          const meta = SOURCE_BADGE[p.source] ?? 'bg-page-bg text-text-secondary'
          return (
            <span
              key={p.branchId}
              className={cn(
                'inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold',
                meta,
              )}
            >
              {SOURCE_LABEL[p.source] ?? p.source}
            </span>
          )
        })}
      </div>
    </li>
  )
}

// ----- Per-branch counts в Результатах -----

function BranchStatsBlock({ jobId, companyId }: { jobId: string; companyId: string }) {
  const statsQuery = useQuery({
    queryKey: ['analyses', jobId, 'branch-stats'],
    queryFn: () => analysesApi.branchStats(jobId),
    staleTime: 30_000,
  })

  const grouped = useMemo(() => groupBranchStats(statsQuery.data ?? []), [statsQuery.data])
  // Если job только что вышел в terminal статус — анализ уже сделан, но
  // analysis_job_reviews могло ещё не зафиксироваться сразу. Показываем загрузку.
  if (statsQuery.isLoading) {
    return <div className="text-xs text-text-tertiary mt-2">Загружаем разбивку…</div>
  }
  if (statsQuery.isError) {
    return (
      <div className="text-xs text-destructive mt-2">
        Не удалось получить разбивку по филиалам.
      </div>
    )
  }
  if (grouped.length === 0) return null

  // Используем уникальный companyId как ключ для invalidate cache — на случай
  // если разные jobs одной компании рендерятся в одной сессии.
  void companyId

  return (
    <div className="mt-2">
      <div className="text-xs uppercase tracking-wide text-text-tertiary mb-2 flex items-center gap-1">
        <Layers size={12} />
        Собрано по филиалам
      </div>
      <ul className="space-y-2">
        {grouped.map((b) => (
          <li
            key={b.branchId}
            className="rounded-xl border border-border-subtle bg-card/40 px-4 py-3"
          >
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0 flex-1">
                {/* Адрес — главный идентификатор (см. ParamsBranchRow выше) */}
                <div className="text-sm font-medium text-text-primary truncate">
                  {b.branchAddress ?? (
                    b.branchName ?? <span className="italic text-text-tertiary">Филиал удалён</span>
                  )}
                </div>
                {b.branchAddress && b.branchName && (
                  <div className="text-xs text-text-tertiary truncate">{b.branchName}</div>
                )}
              </div>
              <div className="text-sm font-semibold text-text-primary shrink-0">
                {b.total.toLocaleString('ru-RU')}{' '}
                <span className="text-xs font-normal text-text-tertiary">
                  {pluralize(b.total, ['отзыв', 'отзыва', 'отзывов'])}
                </span>
              </div>
            </div>
            <div className="mt-2 flex flex-wrap gap-1.5">
              {b.bySource.map((s) => {
                const meta = SOURCE_BADGE[s.source] ?? 'bg-page-bg text-text-secondary'
                return (
                  <span
                    key={s.source}
                    className={cn(
                      'inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-semibold',
                      meta,
                    )}
                  >
                    {SOURCE_LABEL[s.source] ?? s.source}
                    <span className="opacity-70">{s.count.toLocaleString('ru-RU')}</span>
                  </span>
                )
              })}
            </div>
          </li>
        ))}
      </ul>
    </div>
  )
}

interface GroupedBranchStats {
  branchId: string
  branchName: string | null
  branchAddress: string | null
  total: number
  bySource: Array<{ source: string; count: number }>
}

function groupBranchStats(stats: BranchStatsDto[]): GroupedBranchStats[] {
  const map = new Map<string, GroupedBranchStats>()
  for (const s of stats) {
    let entry = map.get(s.branchId)
    if (!entry) {
      entry = {
        branchId: s.branchId,
        branchName: s.branchName,
        branchAddress: s.branchAddress,
        total: 0,
        bySource: [],
      }
      map.set(s.branchId, entry)
    }
    entry.total += s.reviewCount
    entry.bySource.push({ source: s.source, count: s.reviewCount })
  }
  return Array.from(map.values()).sort((a, b) => b.total - a.total)
}

function pluralize(n: number, forms: [string, string, string]): string {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return forms[0]
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return forms[1]
  return forms[2]
}
