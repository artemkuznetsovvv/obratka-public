import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  Activity,
  ArrowLeft,
  ChevronDown,
  ExternalLink,
  Loader2,
  TrendingDown,
  TrendingUp,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { cn } from '@/lib/utils'
import { describeApiError } from '@/api/errors'
import {
  monitoringsApi,
  MONITORING_STATUS_LABEL,
  FREQUENCY_LABEL,
  type MonitoringCycle,
  type MonitoringCycleStatus,
} from '@/api/monitorings'

const CYCLE_STATUS: Record<MonitoringCycleStatus, { label: string; cls: string }> = {
  running: { label: 'Идёт', cls: 'bg-blue-100 text-blue-700' },
  success: { label: 'Успешно', cls: 'bg-emerald-100 text-emerald-700' },
  partial: { label: 'Частично', cls: 'bg-amber-100 text-amber-700' },
  failed: { label: 'Ошибка', cls: 'bg-red-100 text-red-700' },
}

export default function MonitoringDetailPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()

  const detailQuery = useQuery({
    queryKey: ['monitoring', id],
    queryFn: () => monitoringsApi.get(id!),
    enabled: !!id,
    refetchInterval: 15_000,
  })

  const monitoring = detailQuery.data?.monitoring
  const cycles = detailQuery.data?.cycles ?? []

  return (
    <AppLayout
      breadcrumbs={[
        { label: 'Live-мониторинг', to: '/monitoring' },
        { label: monitoring?.companyName ?? '—' },
      ]}
    >
      <div className="max-w-4xl mx-auto">
        <div className="mb-6">
          <Button variant="outline" size="sm" className="gap-2" onClick={() => navigate('/monitoring')}>
            <ArrowLeft size={14} />К мониторингам
          </Button>
        </div>

        {detailQuery.isLoading ? (
          <Card className="p-8 text-text-secondary">Загружаем…</Card>
        ) : detailQuery.isError ? (
          <Card className="p-8 text-destructive">{describeApiError(detailQuery.error)}</Card>
        ) : !monitoring ? (
          <Card className="p-8 text-text-secondary">Мониторинг не найден</Card>
        ) : (
          <>
            <Card className="mb-6 p-6">
              <div className="flex items-start justify-between gap-4 flex-wrap">
                <div className="min-w-0">
                  <h1 className="text-h1 text-text-primary mb-2 flex items-center gap-2">
                    <Activity size={20} className="text-brand" />
                    {monitoring.companyName}
                  </h1>
                  <div className="text-sm text-text-secondary">
                    {MONITORING_STATUS_LABEL[monitoring.status]} · {FREQUENCY_LABEL[monitoring.frequency]} ·
                    окно {monitoring.windowDays} дн. · {monitoring.branches.length}{' '}
                    {pluralize(monitoring.branches.length, ['филиал', 'филиала', 'филиалов'])}
                  </div>
                </div>
                <Button
                  size="sm"
                  className="gap-2"
                  onClick={() =>
                    navigate(`/history/${monitoring.seedJobId}/dashboard?monitoring=${monitoring.id}`)
                  }
                >
                  <ExternalLink size={14} />
                  Открыть дашборд
                </Button>
              </div>
            </Card>

            <h2 className="text-h2 text-text-primary mb-3">История циклов</h2>
            {cycles.length === 0 ? (
              <Card className="p-6 text-text-secondary">Циклов пока нет.</Card>
            ) : (
              <div className="space-y-3">
                {cycles.map((c, i) => {
                  // cycles отсортированы по убыванию номера → следующий в массиве = предыдущий по времени.
                  const prev = cycles[i + 1]
                  return <CycleCard key={c.cycleNumber} cycle={c} prev={prev} />
                })}
              </div>
            )}
          </>
        )}
      </div>
    </AppLayout>
  )
}

function CycleCard({ cycle, prev }: { cycle: MonitoringCycle; prev?: MonitoringCycle }) {
  const [expanded, setExpanded] = useState(false)
  const status = CYCLE_STATUS[cycle.status]
  const isBaseline = cycle.cycleNumber === 0

  const negDelta =
    prev && !isBaseline ? cycle.negativeRatioPp - prev.negativeRatioPp : null

  return (
    <Card className={cn('p-4', cycle.negativeSpikeTriggered && 'border-red-300')}>
      <div className="flex items-start justify-between gap-4 flex-wrap">
        <div>
          <div className="flex items-center gap-2">
            <span className="text-sm font-semibold text-text-primary">
              {isBaseline ? 'Базовый снимок' : `Цикл #${cycle.cycleNumber}`}
            </span>
            <span className={cn('inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold', status.cls)}>
              {cycle.status === 'running' ? (
                <Loader2 size={11} className="mr-1 animate-spin" />
              ) : null}
              {status.label}
            </span>
            {cycle.negativeSpikeTriggered && (
              <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-semibold bg-red-100 text-red-700">
                <TrendingUp size={11} />
                Резкий рост негатива
              </span>
            )}
          </div>
          <div className="mt-1 text-xs text-text-tertiary">
            {formatDateTime(cycle.startedAt)}
            {cycle.finishedAt ? ` → ${formatDateTime(cycle.finishedAt)}` : ' · идёт…'}
          </div>
        </div>

        {!isBaseline && (
          <div className="flex items-center gap-4 text-sm">
            <Metric label="Новых" value={`+${cycle.newReviewCount}`} />
            <Metric label="Всего" value={String(cycle.totalReviewsAtCycle)} />
            <div className="text-right">
              <div className="text-text-tertiary text-xs">Негатив</div>
              <div className="flex items-center gap-1 text-text-primary font-medium">
                {cycle.negativeRatioPp.toFixed(1)}%
                {negDelta !== null && Math.abs(negDelta) >= 0.1 && (
                  <span
                    className={cn(
                      'inline-flex items-center text-xs',
                      negDelta > 0 ? 'text-red-600' : 'text-emerald-600',
                    )}
                  >
                    {negDelta > 0 ? <TrendingUp size={12} /> : <TrendingDown size={12} />}
                    {Math.abs(negDelta).toFixed(1)}
                  </span>
                )}
              </div>
            </div>
          </div>
        )}
      </div>

      {cycle.error && (
        <div className="mt-2 rounded bg-destructive/10 px-3 py-2 text-xs text-destructive">{cycle.error}</div>
      )}

      {(cycle.summary || cycle.recommendations.length > 0) && (
        <div className="mt-3 border-t border-border-subtle pt-2">
          <button
            type="button"
            onClick={() => setExpanded((v) => !v)}
            className="inline-flex items-center gap-1.5 text-xs text-text-secondary hover:text-text-primary"
            aria-expanded={expanded}
          >
            Рекомендации на момент цикла ({cycle.recommendations.length})
            <ChevronDown size={14} className={cn('transition-transform', expanded && 'rotate-180')} />
          </button>
          {expanded && (
            <div className="mt-2 space-y-2">
              {cycle.summary && <p className="text-sm text-text-secondary">{cycle.summary}</p>}
              {cycle.recommendations.map((r, idx) => (
                <div key={idx} className="rounded-lg border border-border-subtle bg-card/40 px-3 py-2">
                  <div className="text-sm font-medium text-text-primary">
                    <span className="text-text-tertiary mr-1">P{r.priority}</span>
                    {r.title}
                  </div>
                  {r.body && <div className="text-xs text-text-secondary mt-0.5">{r.body}</div>}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </Card>
  )
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div className="text-right">
      <div className="text-text-tertiary text-xs">{label}</div>
      <div className="text-text-primary font-medium">{value}</div>
    </div>
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
