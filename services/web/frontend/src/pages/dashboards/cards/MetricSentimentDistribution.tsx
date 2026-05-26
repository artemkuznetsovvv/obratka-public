import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Card } from '@/components/ui/card'
import { metricsApi, type SentimentDistributionMetricDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricErrorCard, MetricSkeletonCard } from './shared/CardParts'

// Метрика М3 «Настроение клиентов» (per-branch).
// Не принимает фильтр sentiments (исключение — см. SentimentDistributionMetricService).
// Принимает sources/stars/period.
export function MetricSentimentDistribution({ branchId }: { branchId: string }) {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()

  const sourcesKey = [...filters.sources].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')

  const q = useQuery({
    queryKey: [
      'metrics',
      jobId,
      'sentiment-distribution',
      branchId,
      filters.periodFrom,
      filters.periodTo,
      sourcesKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.sentimentDistribution(jobId!, {
        branchIds: [branchId],
        from: filters.periodFrom,
        to: filters.periodTo,
        sources: filters.sources,
        stars: filters.stars,
      }),
    enabled: !!jobId && !!branchId,
    staleTime: 30_000,
  })

  if (q.isLoading) return <MetricSkeletonCard />
  if (q.isError) return <MetricErrorCard message={(q.error as Error).message} />
  if (!q.data) return <MetricSkeletonCard />

  return <SentimentView dto={q.data} slug="М3" title="Настроение клиентов" isFetching={q.isFetching && !q.isLoading} />
}

// Shared view М3/О3 — структура и логика одинаковые, разница в slug+title+filter source.
export function SentimentView({
  dto,
  slug,
  title,
  isFetching,
}: {
  dto: SentimentDistributionMetricDto
  slug: string
  title: string
  isFetching: boolean
}) {
  const { totalNonEmpty } = dto

  // Empty state по спеке: «общий «—», полоса и фраза скрыты».
  if (totalNonEmpty === 0) {
    return (
      <Card className={cn('p-5 flex flex-col gap-3', isFetching && 'opacity-70')}>
        <Header slug={slug} title={title} />
        <div className="text-sm text-text-tertiary">Нет данных</div>
      </Card>
    )
  }

  // Точные доли в долях (для правил), целые проценты (для отображения).
  const negFraction = dto.negative / totalNonEmpty
  const neuFraction = dto.neutral / totalNonEmpty
  const posFraction = dto.positive / totalNonEmpty

  const [negPct, neuPct, posPct] = roundToHundred([negFraction, neuFraction, posFraction])

  const verdict = pickVerdict(posFraction, negFraction)

  return (
    <Card
      className={cn(
        'p-5 flex flex-col gap-3 transition-opacity',
        isFetching && 'opacity-70',
      )}
    >
      <Header slug={slug} title={title} />

      {/* Фраза-вывод */}
      <div className={cn('text-xl font-bold', verdict.colorClass)}>{verdict.text}</div>

      {/* Stacked bar: красный → серый → зелёный (слева направо по спеке) */}
      <div className="flex h-7 rounded-full overflow-hidden bg-page-bg" title={`${negPct}% / ${neuPct}% / ${posPct}%`}>
        <Segment pct={negPct} className="bg-rose-500 text-white" label="плохо" />
        <Segment pct={neuPct} className="bg-slate-400 text-white" label="нейтрально" />
        <Segment pct={posPct} className="bg-emerald-500 text-white" label="хорошо" />
      </div>

      {/* Легенда */}
      <div className="flex items-center justify-between gap-2 text-[11px] text-text-secondary">
        <LegendDot color="bg-rose-500" label="Плохо" />
        <LegendDot color="bg-slate-400" label="Нейтрально" />
        <LegendDot color="bg-emerald-500" label="Хорошо" />
      </div>

      <div className="text-[11px] text-text-tertiary">по {totalNonEmpty} отзывам с оценкой LLM</div>
    </Card>
  )
}

function Header({ slug, title }: { slug: string; title: string }) {
  return (
    <div className="flex items-baseline justify-between gap-2">
      <div className="text-sm font-semibold text-text-primary">{title}</div>
      <span className="text-[10px] uppercase tracking-wide text-text-tertiary font-mono">{slug}</span>
    </div>
  )
}

function Segment({ pct, className, label }: { pct: number; className: string; label: string }) {
  if (pct === 0) return null
  return (
    <div
      className={cn(
        'flex items-center justify-center text-[11px] font-semibold tabular-nums',
        className,
      )}
      style={{ width: `${pct}%` }}
      title={`${label}: ${pct}%`}
    >
      {/* Не показываем число если сегмент совсем тонкий — иначе текст вылазит */}
      {pct >= 8 ? `${pct}%` : ''}
    </div>
  )
}

function LegendDot({ color, label }: { color: string; label: string }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className={cn('w-2 h-2 rounded-full', color)} />
      {label}
    </span>
  )
}

// Правила выбора фразы (спека М3). Проверяются в указанном порядке, первое
// сработавшее применяется. Сравниваем по unrounded долям, чтобы граничные
// случаи (например ровно 30%) не зависели от round-up/down артефактов.
function pickVerdict(posFraction: number, negFraction: number): {
  text: string
  colorClass: string
} {
  if (posFraction >= 0.6) return { text: 'Преобладает позитив', colorClass: 'text-emerald-700' }
  if (negFraction >= 0.4) return { text: 'Много негатива', colorClass: 'text-rose-600' }
  if (posFraction >= 0.5) return { text: 'Скорее довольны', colorClass: 'text-emerald-600' }
  if (negFraction >= 0.3) return { text: 'Скорее недовольны', colorClass: 'text-orange-600' }
  return { text: 'Смешанные настроения', colorClass: 'text-text-secondary' }
}

// Округление с гарантией суммы = 100 (largest remainder method). Без этого
// 33.33% × 3 → 33+33+33 = 99, что выглядит криво на полосе из 3 сегментов.
function roundToHundred(fractions: number[]): number[] {
  const raw = fractions.map((f) => f * 100)
  const floors = raw.map(Math.floor)
  let used = floors.reduce((a, b) => a + b, 0)
  const remainders = raw
    .map((r, i) => ({ rem: r - Math.floor(r), i }))
    .sort((a, b) => b.rem - a.rem)
  const out = [...floors]
  for (const x of remainders) {
    if (used >= 100) break
    out[x.i] += 1
    used += 1
  }
  return out
}
