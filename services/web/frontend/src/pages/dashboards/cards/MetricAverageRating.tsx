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

// Метрика 2 «Средний рейтинг» (per-branch).
// Логика подсветки источника + текстовой подписи описана в спеке:
//   gap ≥ 0.5            → подсвечиваем «худший» оранжевым, подпись «Хуже всего на ...»
//   гэп < 0.5 но один источник на ≥ 0.3 выше среднего двух других → «лучший» зелёным
//   иначе                → подсветки нет, «Источники сопоставимы», серый
//
// «Снятые в фильтре источники»: показываем их AVG (он реальный), но
// приглушаем opacity-50 и НЕ учитываем ни в total weighted-avg, ни в логике
// подсветки. Симметрично с М1.
export function MetricAverageRating({ branchId }: { branchId: string }) {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()

  const sentimentsKey = [...filters.sentiments].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')

  const q = useQuery({
    queryKey: [
      'metrics',
      jobId,
      'average-rating',
      branchId,
      filters.periodFrom,
      filters.periodTo,
      sentimentsKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.averageRating(jobId!, {
        branchIds: [branchId],
        from: filters.periodFrom,
        to: filters.periodTo,
        sentiments: filters.sentiments,
        stars: filters.stars,
      }),
    enabled: !!jobId && !!branchId,
    staleTime: 30_000,
  })

  if (q.isLoading) return <MetricSkeletonCard />
  if (q.isError) return <MetricErrorCard message={(q.error as Error).message} />
  if (!q.data) return <MetricSkeletonCard />

  return (
    <AverageRatingView
      dto={q.data}
      selectedSources={filters.sources}
      isFetching={q.isFetching && !q.isLoading}
    />
  )
}

interface DiagnosisResult {
  highlight: 'worst' | 'best' | 'neutral'
  source?: string
  labelTone: 'warning' | 'good' | 'muted' | 'hidden'
  labelText: string
}

function AverageRatingView({
  dto,
  selectedSources,
  isFetching,
}: {
  dto: AverageRatingMetricDto
  selectedSources: string[]
  isFetching: boolean
}) {
  const selectedSet = useMemo(() => new Set(selectedSources), [selectedSources])

  // Считаем weighted-avg по выбранным в фильтре источникам (consistent с М1).
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
        'p-5 flex flex-col gap-3 transition-opacity',
        isFetching && 'opacity-70',
      )}
    >
      <div className="flex items-baseline justify-between gap-2">
        <div className="text-sm font-semibold text-text-primary">Средний рейтинг</div>
        <span className="text-[10px] uppercase tracking-wide text-text-tertiary font-mono">
          М2
        </span>
      </div>

      {/* Главное число */}
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
      <div className="text-[11px] text-text-tertiary -mt-2">по {total.count} отзывам со звёздами</div>

      {/* 3 мини-блока по источникам */}
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

      {/* Подпись */}
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

// Логика подсветки. Применяется только к активным (selected в фильтре + count>0).
//   gap = max - min среди активных
//   gap >= 0.5            → подсвечиваем worst (оранжевый)
//   gap <  0.5 && один источник на ≥ 0.3 выше среднего двух других → best (зелёный)
//   иначе                 → «сопоставимы»
function diagnoseDispersion(
  activeSources: { source: string; average: number | null; count: number }[],
): DiagnosisResult {
  const withData = activeSources.filter(
    (s): s is { source: string; average: number; count: number } => s.average !== null,
  )

  if (withData.length === 0) {
    // Подпись скрыта: нет данных вообще (см. спеку «общий «—», подпись скрывается»).
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

  // gap < 0.5. Проверяем «один источник заметно выше остальных» — отклонение
  // в плюс ≥ 0.3 от среднего двух других.
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

// Единое округление до 1 знака для главного числа и мини-блоков (спека:
// «округление единообразное во всех блоках»). Math.round даёт banker's rounding
// в JS? Нет — стандартный round-half-up для positive. Подходит.
function formatRating(value: number): string {
  return (Math.round(value * 10) / 10).toFixed(1)
}
