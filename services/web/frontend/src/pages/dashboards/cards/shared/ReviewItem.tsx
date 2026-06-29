import { Star } from 'lucide-react'
import type { SentimentReviewItemDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'

const SOURCE_BADGE: Record<string, string> = {
  '2gis': 'bg-emerald-100 text-emerald-700',
  yandex: 'bg-amber-100 text-amber-700',
  google: 'bg-blue-100 text-blue-700',
}

// Карточка одного отзыва: бейдж источника, звёзды, дата, текст.
// Единый рендер для SentimentReviewsDialog (модалка раскрытия М3/О3 — полный
// текст) и MetricTopExamples («Топ примеров» — текст обрезается через maxChars,
// как в PDF-отчёте), чтобы вид отзыва не расходился.
export function ReviewItem({
  review,
  maxChars,
}: {
  review: SentimentReviewItemDto
  // Обрезать текст до N символов с «…» (опц.). Без него — полный текст.
  maxChars?: number
}) {
  const date = formatReviewDate(review.reviewDate)
  const text = maxChars != null ? truncate(review.text, maxChars) : review.text
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
        {text || <span className="italic text-text-tertiary">Пустой текст отзыва</span>}
      </div>
    </li>
  )
}

function truncate(s: string, max: number): string {
  if (!s || s.length <= max) return s
  return s.slice(0, max).trimEnd() + '…'
}

// ДД.ММ.ГГГГ; на невалидной/нераспарсиваемой строке возвращаем как есть
// (new Date не бросает на мусоре — даёт Invalid Date, ловим через getTime()).
export function formatReviewDate(iso: string): string {
  try {
    const d = new Date(iso)
    if (Number.isNaN(d.getTime())) return iso
    return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' })
  } catch {
    return iso
  }
}
