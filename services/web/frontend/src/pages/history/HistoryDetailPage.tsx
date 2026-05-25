import { useMemo } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  AlertCircle,
  ArrowLeft,
  Building2,
  CalendarRange,
  CheckCircle2,
  Clock,
  Loader2,
  RefreshCcw,
  Sparkles,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { analysesApi, type CollectionProgressEntry } from '@/api/analyses'
import { companiesApi } from '@/api/companies'
import { cn } from '@/lib/utils'
import { approximateProgress, isTerminal, SOURCE_LABEL, statusMetaFor } from './analysisStatus'

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
    // Поллинг каждые 3 секунды пока pipeline в не-терминальном состоянии.
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
  const sourceEntries = useMemo<Array<[string, CollectionProgressEntry]>>(
    () => (job ? Object.entries(job.collectionProgress) : []),
    [job],
  )

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
          <div className="space-y-4">
            {/* Header card */}
            <Card className="p-6">
              <div className="flex items-start justify-between gap-4 flex-wrap mb-4">
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2 mb-2 flex-wrap">
                    <span
                      className={cn(
                        'inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold border',
                        meta.badge,
                      )}
                    >
                      {meta.label}
                    </span>
                    {!isTerminal(job.status) && (
                      <Loader2
                        size={14}
                        className="text-text-tertiary animate-spin"
                      />
                    )}
                  </div>
                  <h1 className="text-h2 text-text-primary mb-1 flex items-center gap-2">
                    <Building2 size={18} className="text-text-tertiary" />
                    {companyQuery.data?.name ?? 'Компания…'}
                  </h1>
                  <div className="text-xs text-text-tertiary flex items-center gap-1">
                    <Clock size={12} />
                    Запущен {formatDateTime(job.createdAt)}
                    {job.completedAt && <> · завершён {formatDateTime(job.completedAt)}</>}
                  </div>
                </div>
              </div>

              <ProgressBar value={overallProgress} tone={meta.tone} />
              <div className="mt-2 text-xs text-text-tertiary">
                {isTerminal(job.status)
                  ? 'Готово'
                  : 'Анализ выполняется автоматически — можно закрыть страницу и вернуться позже.'}
              </div>

              {job.error && (
                <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-900 flex items-start gap-2">
                  <AlertCircle size={16} className="mt-0.5 shrink-0 text-rose-700" />
                  <div>
                    <div className="font-medium mb-0.5">Ошибка</div>
                    <div className="text-rose-800">{job.error}</div>
                  </div>
                </div>
              )}
            </Card>

            {/* Per-source breakdown */}
            {sourceEntries.length > 0 && (
              <Card className="p-6">
                <div className="text-h3 text-text-primary mb-3 flex items-center gap-2">
                  <CalendarRange size={16} className="text-text-tertiary" />
                  Прогресс по источникам
                </div>
                <div className="space-y-3">
                  {sourceEntries.map(([source, entry]) => (
                    <SourceProgressRow key={source} source={source} entry={entry} />
                  ))}
                </div>
              </Card>
            )}

            {/* Result section */}
            {job.status === 'completed' || job.status === 'partial' ? (
              <Card className="p-6">
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
                  <div className="rounded-xl border border-border-subtle bg-page-bg/40 p-4">
                    <div className="text-xs uppercase tracking-wide text-text-tertiary mb-1.5 flex items-center gap-1">
                      <Sparkles size={12} />
                      Резюме от AI
                    </div>
                    <div className="text-sm text-text-primary whitespace-pre-line">{job.summary}</div>
                  </div>
                ) : (
                  <div className="text-sm text-text-tertiary">
                    Сводка LLM ещё не получена. Если статус «Частично» — часть данных не собралась,
                    но финальный отчёт может всё равно сформироваться.
                  </div>
                )}
                <div className="mt-4 text-xs text-text-tertiary">
                  Полный дашборд и список рекомендаций — в следующих обновлениях.
                </div>
              </Card>
            ) : null}
          </div>
        )}
      </div>
    </AppLayout>
  )
}

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
  const pct = entry.status === 'completed'
    ? 100
    : entry.status === 'failed'
      ? 100
      : Math.round((entry.progress ?? 0) * 100)
  return (
    <div>
      <div className="flex items-center justify-between gap-2 mb-1">
        <div className="flex items-center gap-2 min-w-0">
          <span className={cn('inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold shrink-0', badgeColor)}>
            {sourceLabel}
          </span>
          <span className={cn('inline-flex items-center px-2 py-0.5 rounded-full text-[10px] font-semibold border', entryMeta.badge)}>
            {entryMeta.label}
          </span>
        </div>
        <span className="text-xs text-text-tertiary">
          {entry.reviewCount !== null
            ? `${entry.reviewCount.toLocaleString('ru-RU')} отзывов`
            : entry.status === 'failed'
              ? '—'
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

// PG отдаёт status источника как pending/running/completed/failed — маппим в наши общие badges.
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
