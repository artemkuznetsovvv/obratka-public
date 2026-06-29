import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Card } from '@/components/ui/card'
import { metricsApi } from '@/api/metrics'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricErrorCard, MetricSkeletonCard } from './shared/CardParts'
import { SentimentView } from './MetricSentimentDistribution'

// О3 «Настроение по сети» — слой «По сети», виден при 3+ филиалах в джобе.
// Тот же compute что М3, branchIds = filter.branches.
export function MetricNetworkSentimentDistribution() {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()

  const sourcesKey = [...filters.sources].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')
  const branchesKey = [...filters.branches].sort().join(',')

  const noBranches = filters.branches.length === 0

  const q = useQuery({
    queryKey: [
      'metrics',
      jobId,
      'network-sentiment-distribution',
      branchesKey,
      filters.periodFrom,
      filters.periodTo,
      sourcesKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.sentimentDistribution(jobId!, {
        branchIds: filters.branches,
        from: filters.periodFrom,
        to: filters.periodTo,
        sources: filters.sources,
        stars: filters.stars,
      }),
    enabled: !!jobId && !noBranches,
    staleTime: 30_000,
  })

  if (noBranches) {
    return (
      <Card className="p-5 min-h-[12rem] flex flex-col gap-3">
        <div className="text-sm font-semibold text-text-primary">Настроение по сети</div>
        <div className="text-sm text-text-tertiary">Снимите фильтр «Филиал», чтобы увидеть сводку.</div>
      </Card>
    )
  }

  if (q.isLoading) return <MetricSkeletonCard minHeight="12rem" />
  if (q.isError) return <MetricErrorCard message={(q.error as Error).message} />
  if (!q.data) return <MetricSkeletonCard minHeight="12rem" />

  return (
    <SentimentView
      dto={q.data}
      title="Настроение по сети"
      isFetching={q.isFetching && !q.isLoading}
      branchIds={filters.branches}
      scopeLabel={`по сети из ${filters.branches.length} ${pluralize(
        filters.branches.length,
        ['филиала', 'филиалов', 'филиалов'],
      )}`}
    />
  )
}

function pluralize(n: number, forms: [string, string, string]): string {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return forms[0]
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return forms[1]
  return forms[2]
}
