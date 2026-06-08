import { useQuery, type UseQueryResult } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Card } from '@/components/ui/card'
import { metricsApi, type SentimentReviewsDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricErrorCard, MetricSkeletonCard } from './shared/CardParts'
import { ReviewItem } from './shared/ReviewItem'

const TOP_LIMIT = 5
// Как в PDF-отчёте (ReportDataAssembler.ExampleTextMax) — длинные отзывы режем,
// чтобы карточка не растягивалась; полный текст доступен в модалке М3/О3.
const EXAMPLE_TEXT_MAX = 600

// Блок «Топ примеров» (ТЗ 4.3): до 5 положительных и до 5 отрицательных отзывов
// филиала за выбранный период. Источник данных — тот же sentiment-reviews
// эндпоинт, что и в PDF-отчёте (ReportDataAssembler.BuildExamplesAsync): два
// независимых запроса (позитив/негатив), limit=5, сортировка review_date DESC.
// Пользовательский фильтр «тональность» (sentiments) НЕ передаём — карточка сама
// про разрез (как М3). Фильтры период/источники/звёзды передаются как обычно.
export function MetricTopExamples({ branchId }: { branchId: string }) {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()

  const sourcesKey = [...filters.sources].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')

  const baseKey = [
    'metrics',
    jobId,
    'top-examples',
    branchId,
    filters.periodFrom,
    filters.periodTo,
    sourcesKey,
    starsKey,
  ]

  const common = {
    branchIds: [branchId],
    from: filters.periodFrom,
    to: filters.periodTo,
    sources: filters.sources,
    stars: filters.stars,
    limit: TOP_LIMIT,
    offset: 0,
  }

  const positive = useQuery({
    queryKey: [...baseKey, 'позитивный'],
    queryFn: () => metricsApi.sentimentReviews(jobId!, { ...common, sentiment: 'позитивный' }),
    enabled: !!jobId && !!branchId,
    staleTime: 30_000,
  })
  const negative = useQuery({
    queryKey: [...baseKey, 'негативный'],
    queryFn: () => metricsApi.sentimentReviews(jobId!, { ...common, sentiment: 'негативный' }),
    enabled: !!jobId && !!branchId,
    staleTime: 30_000,
  })

  // Полный скелет/ошибка — только когда ОБА запроса в этом состоянии; иначе
  // показываем карточку и разруливаем состояние внутри каждой колонки.
  if (positive.isLoading && negative.isLoading) return <MetricSkeletonCard minHeight="12rem" />
  if (positive.isError && negative.isError)
    return <MetricErrorCard message={(positive.error as Error).message} />

  const isFetching =
    (positive.isFetching && !positive.isLoading) || (negative.isFetching && !negative.isLoading)

  return (
    <Card className={cn('p-5 flex flex-col gap-3 transition-opacity', isFetching && 'opacity-70')}>
      <div>
        <div className="text-sm font-semibold text-text-primary">Топ примеров</div>
        <div className="text-[11px] text-text-tertiary mt-0.5">
          До {TOP_LIMIT} свежих положительных и отрицательных отзывов за выбранный период.
        </div>
      </div>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <ExamplesColumn
          title="Положительные"
          titleColor="text-emerald-700"
          query={positive}
          emptyLabel="Нет положительных отзывов за период"
        />
        <ExamplesColumn
          title="Отрицательные"
          titleColor="text-rose-700"
          query={negative}
          emptyLabel="Нет отрицательных отзывов за период"
        />
      </div>
    </Card>
  )
}

function ExamplesColumn({
  title,
  titleColor,
  query,
  emptyLabel,
}: {
  title: string
  titleColor: string
  query: UseQueryResult<SentimentReviewsDto>
  emptyLabel: string
}) {
  return (
    <div>
      <div className={cn('text-xs font-semibold mb-2', titleColor)}>{title}</div>
      {query.isLoading ? (
        <div className="text-xs text-text-tertiary py-2">Загружаем…</div>
      ) : query.isError ? (
        <div className="text-xs text-destructive py-2">Не удалось загрузить.</div>
      ) : !query.data || query.data.items.length === 0 ? (
        <div className="text-xs text-text-tertiary py-2">{emptyLabel}</div>
      ) : (
        <ul className="space-y-3">
          {query.data.items.map((r) => (
            <ReviewItem key={r.id} review={r} maxChars={EXAMPLE_TEXT_MAX} />
          ))}
        </ul>
      )}
    </div>
  )
}
