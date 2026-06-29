import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { metricsApi } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { ReviewItem } from './shared/ReviewItem'

const SENTIMENT_META: Record<
  string,
  { label: string; titleColor: string }
> = {
  'позитивный': { label: 'Хорошо', titleColor: 'text-emerald-700' },
  'нейтральный': { label: 'Нейтрально', titleColor: 'text-slate-600' },
  'негативный': { label: 'Плохо', titleColor: 'text-rose-700' },
}

const PAGE_SIZE = 20

type Sentiment = 'позитивный' | 'нейтральный' | 'негативный'

// Модалка с раскрытием списка отзывов конкретной тональности.
// Используется и М3 (branchIds=[branchId]), и О3 (branchIds=filter.branches).
// Контекст фильтров (period/sources/stars) тянется из DashboardFiltersContext.
export function SentimentReviewsDialog({
  open,
  onOpenChange,
  branchIds,
  sentiment,
  scopeLabel,
}: {
  open: boolean
  onOpenChange: (next: boolean) => void
  branchIds: string[]
  sentiment: Sentiment | null
  // Подпись «в филиале X» / «по сети из N филиалов» — формируется родителем.
  scopeLabel: string
}) {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()
  const [page, setPage] = useState(0)

  // При каждом открытии или смене sentiment'а — обнуляем пагинацию.
  // useEffect не нужен — page сбрасывается извне через ключ компонента;
  // здесь же сбросим если родитель меняет sentiment без закрытия (теоретически).

  const sourcesKey = [...filters.sources].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')
  const branchesKey = [...branchIds].sort().join(',')

  const q = useQuery({
    queryKey: [
      'metrics',
      jobId,
      'sentiment-reviews',
      branchesKey,
      sentiment,
      filters.periodFrom,
      filters.periodTo,
      sourcesKey,
      starsKey,
      page,
    ],
    queryFn: () =>
      metricsApi.sentimentReviews(jobId!, {
        branchIds,
        sentiment: sentiment!,
        from: filters.periodFrom,
        to: filters.periodTo,
        sources: filters.sources,
        stars: filters.stars,
        limit: PAGE_SIZE,
        offset: page * PAGE_SIZE,
      }),
    enabled: open && !!sentiment && branchIds.length > 0,
    staleTime: 30_000,
  })

  const meta = sentiment ? SENTIMENT_META[sentiment] : null

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) setPage(0)
        onOpenChange(next)
      }}
    >
      <DialogContent className="max-w-2xl max-h-[85vh] flex flex-col">
        <DialogHeader>
          <DialogTitle className={cn(meta?.titleColor)}>
            {meta?.label ?? 'Отзывы'}
          </DialogTitle>
          <DialogDescription>{scopeLabel}</DialogDescription>
        </DialogHeader>

        <div className="-mx-2 px-2 overflow-y-auto flex-1 min-h-0">
          {q.isLoading ? (
            <div className="py-8 text-center text-sm text-text-tertiary">Загружаем отзывы…</div>
          ) : q.isError ? (
            <div className="py-8 text-center text-sm text-destructive">
              Не удалось загрузить: {(q.error as Error).message}
            </div>
          ) : !q.data || q.data.items.length === 0 ? (
            <div className="py-8 text-center text-sm text-text-tertiary">
              Отзывов нет
            </div>
          ) : (
            <ul className="space-y-3 py-2">
              {q.data.items.map((r) => (
                <ReviewItem key={r.id} review={r} />
              ))}
            </ul>
          )}
        </div>

        {/* Пагинация: показываем только когда есть смысл (page>0 или hasMore) */}
        {q.data && (q.data.hasMore || page > 0) && (
          <div className="flex items-center justify-between gap-2 border-t border-border-subtle pt-3">
            <Button
              variant="outline"
              size="sm"
              disabled={page === 0 || q.isFetching}
              onClick={() => setPage((p) => Math.max(0, p - 1))}
            >
              Назад
            </Button>
            <span className="text-xs text-text-tertiary">
              Страница {page + 1}
            </span>
            <Button
              variant="outline"
              size="sm"
              disabled={!q.data.hasMore || q.isFetching}
              onClick={() => setPage((p) => p + 1)}
            >
              Вперёд
            </Button>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
