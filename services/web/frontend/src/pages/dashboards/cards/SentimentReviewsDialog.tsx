import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Star } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { metricsApi, type SentimentReviewItemDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'
import { useDashboardFilters } from '../DashboardFiltersContext'

const SOURCE_BADGE: Record<string, string> = {
  '2gis': 'bg-emerald-100 text-emerald-700',
  yandex: 'bg-amber-100 text-amber-700',
  google: 'bg-blue-100 text-blue-700',
}

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

function ReviewItem({ review }: { review: SentimentReviewItemDto }) {
  const date = formatDate(review.reviewDate)
  return (
    <li className="rounded-xl border border-border-subtle bg-card/50 px-4 py-3">
      <div className="flex items-center justify-between gap-2 mb-2 flex-wrap">
        <div className="flex items-center gap-2">
          <span
            className={cn(
              'inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-semibold',
              SOURCE_BADGE[review.source] ?? 'bg-page-bg text-text-secondary',
            )}
          >
            {SOURCE_LABEL[review.source] ?? review.source}
          </span>
          {review.stars != null && (
            <span className="inline-flex items-center gap-0.5 text-xs text-amber-600 tabular-nums">
              {review.stars}
              <Star size={11} className="fill-amber-500 text-amber-500" />
            </span>
          )}
        </div>
        <span className="text-[11px] text-text-tertiary tabular-nums">{date}</span>
      </div>
      <div className="text-sm text-text-primary whitespace-pre-line break-words">
        {review.text || <span className="italic text-text-tertiary">Пустой текст отзыва</span>}
      </div>
    </li>
  )
}

function formatDate(iso: string): string {
  try {
    const d = new Date(iso)
    return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' })
  } catch {
    return iso
  }
}
