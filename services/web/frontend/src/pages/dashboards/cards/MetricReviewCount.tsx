import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowDown, ArrowRight, ArrowUp, AlertCircle } from 'lucide-react'
import { useParams } from 'react-router-dom'
import { Card } from '@/components/ui/card'
import { metricsApi, type ReviewCountMetricDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'
import { useDashboardFilters } from '../DashboardFiltersContext'

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
        branchId,
        from: filters.periodFrom,
        to: filters.periodTo,
        sentiments: filters.sentiments,
        stars: filters.stars,
      }),
    enabled: !!jobId && !!branchId,
    staleTime: 30_000,
  })

  if (q.isLoading) {
    return <SkeletonCard />
  }
  if (q.isError) {
    return <ErrorCard message={(q.error as Error).message} />
  }
  if (!q.data) {
    return <SkeletonCard />
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
      <div className="flex items-baseline justify-between gap-2">
        <div className="text-sm font-semibold text-text-primary">
          Количество отзывов
        </div>
        <span className="text-[10px] uppercase tracking-wide text-text-tertiary font-mono">
          М1
        </span>
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

// Стрелка + абсолютная разница к предыдущему периоду такой же длительности.
// Цвета: рост = зелёный, падение = красный, без изменений = серый.
// «—» когда нет данных за предыдущий период (или строка источника снята).
//
// Граничный случай «обе нули»: если current=0 и previous=0, диапазон не
// информативен (нет данных в обоих периодах). Спека просит прочерк в таких
// случаях — даём «—» (это покрывает и «нет данных за prev», и «реальный 0=0»).
function TrendLine({
  current,
  previous,
  hasPrev,
  size,
}: {
  current: number
  previous: number
  hasPrev: boolean
  size: 'sm' | 'lg'
}) {
  if (!hasPrev || (current === 0 && previous === 0)) {
    return (
      <span
        className={cn(
          'inline-flex items-center text-text-tertiary',
          size === 'lg' ? 'text-sm' : 'text-xs',
        )}
        title={hasPrev ? 'Нет отзывов ни за выбранный, ни за предыдущий период' : 'Период не выбран — тренд недоступен'}
      >
        —
      </span>
    )
  }

  const diff = current - previous
  const Icon = diff > 0 ? ArrowUp : diff < 0 ? ArrowDown : ArrowRight
  const color =
    diff > 0
      ? 'text-emerald-600'
      : diff < 0
      ? 'text-rose-600'
      : 'text-text-tertiary'

  const absDiff = Math.abs(diff).toLocaleString('ru-RU')
  const sign = diff > 0 ? '+' : diff < 0 ? '−' : ''
  return (
    <span
      className={cn(
        'inline-flex items-center gap-0.5 tabular-nums',
        color,
        size === 'lg' ? 'text-sm font-medium' : 'text-xs',
      )}
      title={`предыдущий период: ${previous.toLocaleString('ru-RU')}`}
    >
      <Icon size={size === 'lg' ? 14 : 12} strokeWidth={2.5} />
      {sign}
      {absDiff}
    </span>
  )
}

function SkeletonCard() {
  return (
    <Card className="p-5 min-h-[14rem] flex items-center justify-center text-xs text-text-tertiary">
      Загружаем…
    </Card>
  )
}

function ErrorCard({ message }: { message: string }) {
  return (
    <Card className="p-5 border-rose-200 bg-rose-50">
      <div className="flex items-start gap-2 text-sm">
        <AlertCircle size={16} className="text-rose-700 shrink-0 mt-0.5" />
        <div className="min-w-0">
          <div className="font-semibold text-rose-900 mb-0.5">
            Не удалось посчитать метрику
          </div>
          <div className="text-rose-800 text-xs break-words">{message}</div>
        </div>
      </div>
    </Card>
  )
}
