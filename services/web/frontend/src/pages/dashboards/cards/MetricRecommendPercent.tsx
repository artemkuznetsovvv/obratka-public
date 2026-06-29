import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { ArrowDown, ArrowRight, ArrowUp } from 'lucide-react'
import { Card } from '@/components/ui/card'
import { metricsApi, type RecommendPercentMetricDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricErrorCard, MetricSkeletonCard } from './shared/CardParts'

// Метрика М6 «Сколько клиентов рекомендуют» — доля overall_sentiment=
// позитивный от total non-empty за период по выбранному филиалу.
// Применяет: branch, period, sources, stars. Sentiments не передаётся.
export function MetricRecommendPercent({ branchId }: { branchId: string }) {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()

  const sourcesKey = [...filters.sources].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')

  const q = useQuery({
    queryKey: [
      'metrics',
      jobId,
      'recommend-percent',
      branchId,
      filters.periodFrom,
      filters.periodTo,
      sourcesKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.recommendPercent(jobId!, {
        branchIds: [branchId],
        from: filters.periodFrom,
        to: filters.periodTo,
        sources: filters.sources,
        stars: filters.stars,
      }),
    enabled: !!jobId && !!branchId,
    staleTime: 30_000,
  })

  if (q.isLoading) return <MetricSkeletonCard minHeight="14rem" />
  if (q.isError) return <MetricErrorCard message={(q.error as Error).message} />
  if (!q.data) return <MetricSkeletonCard minHeight="14rem" />

  return <RecommendPercentView dto={q.data} isFetching={q.isFetching && !q.isLoading} />
}

function RecommendPercentView({
  dto,
  isFetching,
}: {
  dto: RecommendPercentMetricDto
  isFetching: boolean
}) {
  const { current, previous, hasPreviousPeriod } = dto

  // Empty state: 0 отзывов с непустым sentiment.
  if (current.totalNonEmpty === 0) {
    return (
      <Card className={cn('p-5 flex flex-col gap-2 min-h-[14rem]', isFetching && 'opacity-70')}>
        <Header />
        <div className="text-sm text-text-tertiary">Нет данных</div>
      </Card>
    )
  }

  // Доли — точные для логики правил, целые проценты для отображения.
  const currentFraction = current.positive / current.totalNonEmpty
  const currentPct = Math.round(currentFraction * 100)

  const previousFraction = hasPreviousPeriod && previous.totalNonEmpty > 0
    ? previous.positive / previous.totalNonEmpty
    : null
  const previousPct = previousFraction === null ? null : Math.round(previousFraction * 100)

  const verdict = pickVerdict(currentFraction)
  const trend = computeTrend(currentPct, previousPct)

  return (
    <Card
      className={cn(
        'p-5 flex flex-col gap-3 transition-opacity min-h-[14rem]',
        isFetching && 'opacity-70',
      )}
    >
      <Header />

      {/* Словесная оценка — главный элемент */}
      <div className={cn('text-xl font-bold leading-tight', verdict.colorClass)}>
        {verdict.text}
      </div>

      {/* Крупный процент с подписью */}
      <div>
        <div className="text-[40px] leading-none font-bold text-text-primary tabular-nums">
          {currentPct}%
        </div>
        <div className="text-[11px] text-text-tertiary mt-1">довольны и рекомендуют</div>
      </div>

      {/* Строка тренда */}
      <TrendLine trend={trend} currentPct={currentPct} previousPct={previousPct} />

      <div className="text-[11px] text-text-tertiary mt-auto">
        по {current.totalNonEmpty} {pluralize(current.totalNonEmpty, ['отзыву', 'отзывам', 'отзывам'])} с оценкой LLM
      </div>
    </Card>
  )
}

function Header() {
  return (
    <div className="text-sm font-semibold text-text-primary">Сколько клиентов рекомендуют</div>
  )
}

// Таблица фраз из спеки М6. Границы по unrounded fraction:
//   ≥ 80%  → «Почти все довольны»  (тёмно-зелёный)
//   ≥ 60%  → «Большинство довольны» (зелёный)
//   ≥ 40%  → «Доволен примерно каждый второй» (серый)
//   ≥ 20%  → «Доволен только каждый третий»   (оранжевый)
//   < 20%  → «Почти никто не доволен»         (красный)
// «Граничные значения попадают в верхнюю категорию» (спека) — >= даёт это.
function pickVerdict(fraction: number): { text: string; colorClass: string } {
  if (fraction >= 0.8) return { text: 'Почти все довольны', colorClass: 'text-emerald-700' }
  if (fraction >= 0.6) return { text: 'Большинство довольны', colorClass: 'text-emerald-600' }
  if (fraction >= 0.4) return { text: 'Доволен примерно каждый второй', colorClass: 'text-slate-600' }
  if (fraction >= 0.2) return { text: 'Доволен только каждый третий', colorClass: 'text-orange-600' }
  return { text: 'Почти никто не доволен', colorClass: 'text-rose-600' }
}

type TrendTone = 'good' | 'bad' | 'flat' | 'unavailable'
interface TrendState {
  tone: TrendTone
  diff: number  // в процентных пунктах
}

function computeTrend(currentPct: number, previousPct: number | null): TrendState {
  if (previousPct === null) return { tone: 'unavailable', diff: 0 }
  const diff = currentPct - previousPct
  if (diff >= 3) return { tone: 'good', diff }
  if (diff <= -3) return { tone: 'bad', diff }
  return { tone: 'flat', diff }
}

function TrendLine({
  trend,
  currentPct,
  previousPct,
}: {
  trend: TrendState
  currentPct: number
  previousPct: number | null
}) {
  const tooltip =
    previousPct === null
      ? undefined
      : `Прошлый период: ${previousPct}%. Сейчас: ${currentPct}%`

  if (trend.tone === 'unavailable') {
    return <div className="text-xs text-text-tertiary">— нет данных за предыдущий период</div>
  }
  if (trend.tone === 'flat') {
    return (
      <div className="flex items-center gap-1 text-xs text-text-tertiary" title={tooltip}>
        <ArrowRight size={12} strokeWidth={2.5} />
        Без изменений к прошлому периоду
      </div>
    )
  }
  const Icon = trend.tone === 'good' ? ArrowUp : ArrowDown
  const color = trend.tone === 'good' ? 'text-emerald-600' : 'text-rose-600'
  const verb = trend.tone === 'good' ? 'больше' : 'меньше'
  const abs = Math.abs(trend.diff)
  return (
    <div
      className={cn('flex items-center gap-1 text-xs font-medium tabular-nums', color)}
      title={tooltip}
    >
      <Icon size={12} strokeWidth={2.5} />
      Стало {verb} на {abs}% к прошлому периоду
    </div>
  )
}

function pluralize(n: number, forms: [string, string, string]): string {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return forms[0]
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return forms[1]
  return forms[2]
}
