import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Card } from '@/components/ui/card'
import { metricsApi } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricErrorCard, MetricSkeletonCard, TrendLine } from './shared/CardParts'

// О1 «Всего отзывов по сети» — слой общих метрик (виден при 3+ филиалах в джобе).
// Реюзает тот же endpoint /metrics/review-count, что и М1, но передаёт все
// выбранные в фильтре филиалы (filters.branches), а не один. Decomposition
// по источникам отображается компактнее (это сводная метрика, не per-branch).
//
// Поведение по фильтрам:
//   period / sentiments / stars / branches — все передаются в API.
//   sources — фронт применяет к total локально (см. М1, тот же принцип).
//
// Если filters.branches пуст (юзер снял всех) — empty state, без запроса.
export function MetricNetworkTotalReviews() {
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
      'network-total-reviews',
      branchesKey,
      filters.periodFrom,
      filters.periodTo,
      sentimentsKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.reviewCount(jobId!, {
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
        <div className="text-sm text-text-tertiary">Снимите фильтр «Филиал», чтобы увидеть сумму.</div>
      </Card>
    )
  }

  if (q.isLoading) return <MetricSkeletonCard minHeight="12rem" />
  if (q.isError) return <MetricErrorCard message={(q.error as Error).message} />
  if (!q.data) return <MetricSkeletonCard minHeight="12rem" />

  const dto = q.data
  const selectedSourceSet = new Set(filters.sources)

  // Total пересчитываем на фронте по выбранным источникам (как в М1).
  const totalCurrent = dto.bySource
    .filter((s) => selectedSourceSet.has(s.source))
    .reduce((sum, s) => sum + s.current, 0)
  const totalPrevious = dto.bySource
    .filter((s) => selectedSourceSet.has(s.source))
    .reduce((sum, s) => sum + s.previous, 0)

  return (
    <Card
      className={cn(
        'p-5 flex flex-col gap-3 transition-opacity min-h-[12rem]',
        q.isFetching && !q.isLoading && 'opacity-70',
      )}
    >
      <Header />
      <div>
        <div className="text-[40px] leading-none font-bold text-text-primary">
          {totalCurrent.toLocaleString('ru-RU')}
        </div>
        <div className="text-[11px] text-text-tertiary mt-1">
          по {filters.branches.length} {pluralize(filters.branches.length, ['филиалу', 'филиалам', 'филиалам'])}
          {' '}за выбранный период
        </div>
        <div className="mt-2">
          <TrendLine
            current={totalCurrent}
            previous={totalPrevious}
            hasPrev={dto.hasPreviousPeriod}
            size="lg"
          />
        </div>
      </div>
    </Card>
  )
}

function Header() {
  return (
    <div className="flex items-baseline justify-between gap-2">
      <div className="text-sm font-semibold text-text-primary">
        Всего отзывов по сети
      </div>
      <span className="text-[10px] uppercase tracking-wide text-text-tertiary font-mono">
        О1
      </span>
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
