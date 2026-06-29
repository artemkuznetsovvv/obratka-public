import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Card } from '@/components/ui/card'
import { metricsApi, type ReviewCountMetricDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricErrorCard, MetricSkeletonCard, TrendLine } from './shared/CardParts'

const SOURCE_BADGE: Record<string, string> = {
  '2gis': 'bg-emerald-100 text-emerald-700',
  yandex: 'bg-amber-100 text-amber-700',
  google: 'bg-blue-100 text-blue-700',
}

// Метрика 1: «Количество отзывов» с декомпозицией по 3 источникам и трендом
// к предыдущему периоду такой же длительности.
//
// Поведение по фильтрам:
//  - period → передаётся в API, бэк фильтрует SQL по review_date.
//  - branch → этот компонент рендерится per-branch, branchId фиксирован.
//  - sources → НЕ передаётся в API (бэк всегда возвращает 3 источника).
//    UI обнуляет «снятые» строки и пересчитывает total из выбранных. См. спеку:
//    «3 строки всегда, снятые = 0; общее = sum выбранных».
//  - sentiments, stars → передаются как CSV в query.
export function MetricReviewCount({ branchId }: { branchId: string }) {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()

  // queryKey содержит все параметры запроса — TanStack автоматически
  // перефетчит при изменении любого фильтра.
  const sentimentsKey = [...filters.sentiments].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')

  const q = useQuery({
    queryKey: [
      'metrics',
      jobId,
      'review-count',
      branchId,
      filters.periodFrom,
      filters.periodTo,
      sentimentsKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.reviewCount(jobId!, {
        branchIds: [branchId],
        from: filters.periodFrom,
        to: filters.periodTo,
        sentiments: filters.sentiments,
        stars: filters.stars,
      }),
    enabled: !!jobId && !!branchId,
    staleTime: 30_000,
  })

  if (q.isLoading) {
    return <MetricSkeletonCard />
  }
  if (q.isError) {
    return <MetricErrorCard message={(q.error as Error).message} />
  }
  if (!q.data) {
    return <MetricSkeletonCard />
  }
  return (
    <ReviewCountView
      dto={q.data}
      selectedSources={filters.sources}
      isFetching={q.isFetching && !q.isLoading}
    />
  )
}

function ReviewCountView({
  dto,
  selectedSources,
  isFetching,
}: {
  dto: ReviewCountMetricDto
  selectedSources: string[]
  isFetching: boolean
}) {
  const selectedSet = useMemo(() => new Set(selectedSources), [selectedSources])

  // Total пересчитываем на фронте: sum только по выбранным источникам.
  // Иначе backend ничего не знает о фильтре sources (см. комментарий в файле).
  const totalCurrent = useMemo(
    () =>
      dto.bySource
        .filter((s) => selectedSet.has(s.source))
        .reduce((sum, s) => sum + s.current, 0),
    [dto.bySource, selectedSet],
  )
  const totalPrevious = useMemo(
    () =>
      dto.bySource
        .filter((s) => selectedSet.has(s.source))
        .reduce((sum, s) => sum + s.previous, 0),
    [dto.bySource, selectedSet],
  )

  return (
    <Card
      className={cn(
        'p-5 flex flex-col gap-4 transition-opacity',
        isFetching && 'opacity-70',
      )}
    >
      {/* Заголовок карточки */}
      <div className="text-sm font-semibold text-text-primary">
        Количество отзывов
      </div>

      {/* Блок 1: крупное число + тренд */}
      <div>
        <div className="text-[40px] leading-none font-bold text-text-primary">
          {totalCurrent.toLocaleString('ru-RU')}
        </div>
        <div className="text-[11px] text-text-tertiary mt-1">за выбранный период</div>
        <div className="mt-2">
          <TrendLine
            current={totalCurrent}
            previous={totalPrevious}
            hasPrev={dto.hasPreviousPeriod}
            size="lg"
          />
        </div>
      </div>

      {/* Блок 2: декомпозиция по 3 источникам (всегда в порядке 2gis → yandex → google) */}
      <div className="border-t border-border-subtle pt-3 space-y-1.5">
        {dto.bySource.map((s) => {
          const isSelected = selectedSet.has(s.source)
          const shownCurrent = isSelected ? s.current : 0
          const shownPrevious = isSelected ? s.previous : 0
          return (
            <div key={s.source} className="flex items-center justify-between gap-2 py-0.5">
              <span
                className={cn(
                  'inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-semibold shrink-0',
                  SOURCE_BADGE[s.source] ?? 'bg-page-bg text-text-secondary',
                  !isSelected && 'opacity-50',
                )}
                title={!isSelected ? 'Источник снят в фильтре' : undefined}
              >
                {SOURCE_LABEL[s.source] ?? s.source}
              </span>
              <div className="flex items-center gap-2 text-sm">
                <span
                  className={cn(
                    'font-semibold tabular-nums',
                    isSelected ? 'text-text-primary' : 'text-text-tertiary',
                  )}
                >
                  {shownCurrent.toLocaleString('ru-RU')}
                </span>
                <TrendLine
                  current={shownCurrent}
                  previous={shownPrevious}
                  hasPrev={dto.hasPreviousPeriod && isSelected}
                  size="sm"
                />
              </div>
            </div>
          )
        })}
      </div>
    </Card>
  )
}

// (TrendLine / SkeletonCard / ErrorCard вынесены в shared/CardParts —
//  переиспользуются метрикой О1 «Всего по сети».)
