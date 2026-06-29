import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Star } from 'lucide-react'
import { Card } from '@/components/ui/card'
import { metricsApi, type AverageRatingMetricDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricErrorCard, MetricSkeletonCard } from './shared/CardParts'

// О2 «Средний рейтинг по сети» — слой общих метрик (виден при 3+ филиалах).
// Реюзает тот же endpoint /metrics/average-rating, что и М2, но передаёт
// все выбранные в фильтре филиалы. Логика подсветки источника + подпись —
// идентичны М2 (см. MetricAverageRating.tsx).
//
// Если filter.branches пуст — empty state без запроса в API.
export function MetricNetworkAverageRating() {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()

  const sentimentsKey = [...filters.sentiments].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')
  const branchesKey = [...filters.branches].sort().join(',')

  const noBranches = filters.branches.length === 0

  const q = useQuery({
    queryKey: [
      'metrics',
      jobId,
      'network-average-rating',
      branchesKey,
      filters.periodFrom,
      filters.periodTo,
      sentimentsKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.averageRating(jobId!, {
        branchIds: filters.branches,
        from: filters.periodFrom,
        to: filters.periodTo,
        sentiments: filters.sentiments,
        stars: filters.stars,
      }),
    enabled: !!jobId && !noBranches,
    staleTime: 30_000,
  })

  if (noBranches) {
    return (
      <Card className="p-5 min-h-[12rem] flex flex-col justify-between">
        <Header />
        <div className="text-sm text-text-tertiary">Снимите фильтр «Филиал», чтобы увидеть сводку.</div>
      </Card>
    )
  }

  if (q.isLoading) return <MetricSkeletonCard minHeight="12rem" />
  if (q.isError) return <MetricErrorCard message={(q.error as Error).message} />
  if (!q.data) return <MetricSkeletonCard minHeight="12rem" />

  return (
    <NetworkAverageRatingView
      dto={q.data}
      selectedSources={filters.sources}
      branchCount={filters.branches.length}
      isFetching={q.isFetching && !q.isLoading}
    />
  )
}

function Header() {
  return (
    <div className="text-sm font-semibold text-text-primary">Средний рейтинг по сети</div>
  )
}

interface DiagnosisResult {
  highlight: 'worst' | 'best' | 'neutral'
  source?: string
  labelTone: 'warning' | 'good' | 'muted' | 'hidden'
  labelText: string
}

function NetworkAverageRatingView({
  dto,
  selectedSources,
  branchCount,
  isFetching,
}: {
  dto: AverageRatingMetricDto
  selectedSources: string[]
  branchCount: number
  isFetching: boolean
}) {
  const selectedSet = useMemo(() => new Set(selectedSources), [selectedSources])

  // Total weighted-avg только по выбранным в фильтре source'ам (симметрично М1/М2).
  const activeSources = useMemo(
    () => dto.bySource.filter((s) => selectedSet.has(s.source) && s.average !== null && s.count > 0),
    [dto.bySource, selectedSet],
  )

  const total = useMemo<{ avg: number | null; count: number }>(() => {
    const count = activeSources.reduce((a, s) => a + s.count, 0)
    if (count === 0) return { avg: null, count: 0 }
    const sum = activeSources.reduce((a, s) => a + (s.average ?? 0) * s.count, 0)
    return { avg: sum / count, count }
  }, [activeSources])

  const diagnosis = useMemo(() => diagnoseDispersion(activeSources), [activeSources])

  return (
    <Card
      className={cn(
        'p-5 flex flex-col gap-3 transition-opacity min-h-[12rem]',
        isFetching && 'opacity-70',
      )}
    >
      <Header />

      <div className="flex items-baseline gap-2">
        {total.avg === null ? (
          <>
            <span className="text-[40px] leading-none font-bold text-text-tertiary">—</span>
            <Star size={20} className="text-text-tertiary fill-text-tertiary/30" />
          </>
        ) : (
          <>
            <span className="text-[40px] leading-none font-bold text-text-primary tabular-nums">
              {formatRating(total.avg)}
            </span>
            <Star size={20} className="text-amber-500 fill-amber-500" />
          </>
        )}
      </div>
      <div className="text-[11px] text-text-tertiary -mt-2">
        по {total.count} отзывам со звёздами в {branchCount}{' '}
        {pluralize(branchCount, ['филиале', 'филиалах', 'филиалах'])}
      </div>

      <div className="border-t border-border-subtle pt-3 grid grid-cols-3 gap-2">
        {dto.bySource.map((s) => {
          const isSelected = selectedSet.has(s.source)
          const isHighlightedWorst = diagnosis.highlight === 'worst' && diagnosis.source === s.source
          const isHighlightedBest = diagnosis.highlight === 'best' && diagnosis.source === s.source
          return (
            <div
              key={s.source}
              title={!isSelected ? 'Источник снят в фильтре' : undefined}
              className={cn(
                'rounded-lg border px-2 py-2 flex flex-col items-center justify-center gap-1 transition-colors',
                isHighlightedWorst && 'border-orange-400 bg-orange-50',
                isHighlightedBest && 'border-emerald-400 bg-emerald-50',
                !isHighlightedWorst && !isHighlightedBest && 'border-border-subtle bg-card',
                !isSelected && 'opacity-50',
              )}
            >
              <span className="text-[10px] uppercase tracking-wide text-text-tertiary">
                {SOURCE_LABEL[s.source] ?? s.source}
              </span>
              {s.average === null ? (
                <span className="text-base font-semibold text-text-tertiary">—</span>
              ) : (
                <div className="flex items-baseline gap-1">
                  <span className="text-base font-semibold text-text-primary tabular-nums">
                    {formatRating(s.average)}
                  </span>
                  <Star size={11} className="text-amber-500 fill-amber-500" />
                </div>
              )}
            </div>
          )
        })}
      </div>

      {diagnosis.labelTone !== 'hidden' && (
        <div
          className={cn(
            'text-xs',
            diagnosis.labelTone === 'warning' && 'text-orange-700',
            diagnosis.labelTone === 'good' && 'text-emerald-700',
            diagnosis.labelTone === 'muted' && 'text-text-tertiary',
          )}
        >
          {diagnosis.labelText}
        </div>
      )}
    </Card>
  )
}

// Та же логика подсветки что в М2 (см. MetricAverageRating.tsx). Будущее
// извлечение в shared/ оправдано после третьего использования — пока два.
function diagnoseDispersion(
  activeSources: { source: string; average: number | null; count: number }[],
): DiagnosisResult {
  const withData = activeSources.filter(
    (s): s is { source: string; average: number; count: number } => s.average !== null,
  )

  if (withData.length === 0) {
    return { highlight: 'neutral', labelTone: 'hidden', labelText: '' }
  }
  if (withData.length === 1) {
    return { highlight: 'neutral', labelTone: 'muted', labelText: 'Источники сопоставимы' }
  }

  const min = withData.reduce((a, s) => (s.average < a.average ? s : a))
  const max = withData.reduce((a, s) => (s.average > a.average ? s : a))
  const gap = max.average - min.average

  if (gap >= 0.5) {
    return {
      highlight: 'worst',
      source: min.source,
      labelTone: 'warning',
      labelText: `Хуже всего на ${SOURCE_LABEL[min.source] ?? min.source}`,
    }
  }

  for (const s of withData) {
    const others = withData.filter((o) => o.source !== s.source)
    if (others.length === 0) continue
    const othersAvg = others.reduce((a, o) => a + o.average, 0) / others.length
    if (s.average - othersAvg >= 0.3) {
      return {
        highlight: 'best',
        source: s.source,
        labelTone: 'good',
        labelText: `Лучше всего на ${SOURCE_LABEL[s.source] ?? s.source}`,
      }
    }
  }

  return { highlight: 'neutral', labelTone: 'muted', labelText: 'Источники сопоставимы' }
}

function formatRating(value: number): string {
  return (Math.round(value * 10) / 10).toFixed(1)
}

function pluralize(n: number, forms: [string, string, string]): string {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return forms[0]
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return forms[1]
  return forms[2]
}
