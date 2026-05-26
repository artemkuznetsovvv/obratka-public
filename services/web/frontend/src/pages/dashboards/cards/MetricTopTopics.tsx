import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Card } from '@/components/ui/card'
import { metricsApi, type TopicAggregateDto, type TopTopicsMetricDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricErrorCard, MetricSkeletonCard } from './shared/CardParts'

// Метрика М5 «О чём говорят чаще всего» — топ-3 темы за выбранный период
// с распределением pos/neg внутри каждой темы.
// Применяет: branch, period, sources, stars. Sentiments — не передаётся
// (метрика сама про разрез по тональности).
export function MetricTopTopics({ branchId }: { branchId: string }) {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()

  const sourcesKey = [...filters.sources].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')

  const q = useQuery({
    queryKey: [
      'metrics',
      jobId,
      'top-topics',
      branchId,
      filters.periodFrom,
      filters.periodTo,
      sourcesKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.topTopics(jobId!, {
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

  return <TopTopicsView dto={q.data} isFetching={q.isFetching && !q.isLoading} />
}

function TopTopicsView({
  dto,
  isFetching,
}: {
  dto: TopTopicsMetricDto
  isFetching: boolean
}) {
  // Empty state: 0 тем (нет отзывов с непустым aspects).
  if (dto.topics.length === 0) {
    return (
      <Card className={cn('p-5 flex flex-col gap-2 min-h-[14rem]', isFetching && 'opacity-70')}>
        <Header />
        <div className="text-sm text-text-tertiary">
          Недостаточно данных, чтобы выделить темы
        </div>
      </Card>
    )
  }

  const top1 = dto.topics[0]
  const top1Verdict = topicVerdict(top1)
  const headline = pickHeadline(top1, top1Verdict)

  return (
    <Card
      className={cn(
        'p-5 flex flex-col gap-3 transition-opacity min-h-[14rem]',
        isFetching && 'opacity-70',
      )}
    >
      <Header />

      {/* Главная фраза-вывод — единственный цветной акцент */}
      <div className={cn('text-lg font-bold leading-tight', headline.colorClass)}>
        {headline.text}
      </div>

      {/* Топ-3 строки тем */}
      <ul className="space-y-2.5 mt-1">
        {dto.topics.map((t) => (
          <TopicRow key={t.topic} topic={t} totalInPeriod={dto.totalReviewsInPeriod} />
        ))}
      </ul>
    </Card>
  )
}

function Header() {
  return (
    <div className="text-sm font-semibold text-text-primary">О чём говорят чаще всего</div>
  )
}

function TopicRow({
  topic,
  totalInPeriod,
}: {
  topic: TopicAggregateDto
  totalInPeriod: number
}) {
  const verdict = topicVerdict(topic)
  const sharePct = totalInPeriod === 0
    ? 0
    : Math.round((topic.reviewCount / totalInPeriod) * 100)

  // Полоса pos/neg внутри темы (нейтральные исключены из соотношения по спеке).
  const tonalTotal = topic.positiveMentions + topic.negativeMentions
  const negPct = tonalTotal === 0 ? 0 : (topic.negativeMentions / tonalTotal) * 100
  const posPct = tonalTotal === 0 ? 0 : 100 - negPct

  return (
    <li className="flex items-center gap-3 text-text-primary">
      <div className="min-w-0 flex-1">
        <div className="text-sm font-medium text-text-secondary truncate" title={topic.topic}>
          {topic.topic}
        </div>
        <div className="text-[11px] text-text-tertiary">{verdict.label}</div>
      </div>

      <div
        className="w-20 sm:w-24 h-2 rounded-full overflow-hidden bg-page-bg flex shrink-0"
        title={`${topic.positiveMentions} хвалят · ${topic.negativeMentions} ругают${
          tonalTotal === 0 ? ' (нет тональных упоминаний)' : ''
        }`}
      >
        {tonalTotal === 0 ? (
          <div className="flex-1 bg-slate-300" />
        ) : (
          <>
            {negPct > 0 && <div className="bg-rose-500" style={{ width: `${negPct}%` }} />}
            {posPct > 0 && <div className="bg-emerald-500" style={{ width: `${posPct}%` }} />}
          </>
        )}
      </div>

      <div
        className="w-10 text-right text-sm font-semibold text-text-secondary tabular-nums shrink-0"
        title={`${topic.reviewCount} из ${totalInPeriod} отзывов`}
      >
        {sharePct}%
      </div>
    </li>
  )
}

interface TopicVerdict {
  label: string
  posShare: number  // в долях, 0..1
  negShare: number
}

// Вердикт под названием темы (спека): по соотношению pos/neg внутри темы
// (нейтральные исключены).
function topicVerdict(topic: TopicAggregateDto): TopicVerdict {
  const tonalTotal = topic.positiveMentions + topic.negativeMentions
  if (tonalTotal === 0) {
    return { label: 'отзывы поровну', posShare: 0, negShare: 0 }
  }
  const posShare = topic.positiveMentions / tonalTotal
  const negShare = topic.negativeMentions / tonalTotal
  let label: string
  if (negShare >= 0.6) label = 'чаще ругают'
  else if (posShare >= 0.6) label = 'чаще хвалят'
  else label = 'отзывы поровну'
  return { label, posShare, negShare }
}

// Главная фраза-вывод (по топ-1, спека): проверяем правила по порядку.
function pickHeadline(
  topic: TopicAggregateDto,
  verdict: TopicVerdict,
): { text: string; colorClass: string } {
  if (verdict.negShare >= 0.6) {
    return {
      text: `Главная боль — ${topic.topic}`,
      colorClass: 'text-rose-700',
    }
  }
  if (verdict.posShare >= 0.7) {
    return {
      text: `Главная сила — ${topic.topic}`,
      colorClass: 'text-emerald-700',
    }
  }
  return {
    text: `Чаще всего обсуждают ${topic.topic}`,
    colorClass: 'text-text-primary',
  }
}
