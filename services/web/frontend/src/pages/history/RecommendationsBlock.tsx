import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ChevronDown, Lightbulb, MessageSquareQuote, TrendingUp } from 'lucide-react'
import { analysesApi, type RecommendationDto } from '@/api/analyses'
import { cn } from '@/lib/utils'

// Список LLM-рекомендаций по результатам анализа. Рендерится в HistoryDetailPage
// прямо под «Резюме от AI». Сортировка — sort_order ASC (бэк уже отдаёт в
// нужном порядке).
export function RecommendationsBlock({ jobId }: { jobId: string }) {
  const q = useQuery({
    queryKey: ['analyses', jobId, 'recommendations'],
    queryFn: () => analysesApi.recommendations(jobId),
    staleTime: 60_000,
  })

  if (q.isLoading) {
    return (
      <div className="rounded-xl border border-border-subtle bg-page-bg/40 p-4 text-xs text-text-tertiary">
        Загружаем рекомендации…
      </div>
    )
  }

  if (q.isError) {
    // Тихая ошибка: блок «Резюме» уже показывает основную информацию.
    // Если рекомендации не доступны (503/прочее) — не паникуем, просто не
    // показываем секцию. Логируем в консоль для диагностики.
    console.warn('Recommendations failed', q.error)
    return null
  }

  const items = q.data?.items ?? []
  if (items.length === 0) {
    return (
      <div className="rounded-xl border border-border-subtle bg-page-bg/40 p-4 text-xs text-text-tertiary">
        Рекомендации не сгенерированы по этому анализу.
      </div>
    )
  }

  // Глобальная группировка по приоритету: Высокий → Средний → Низкий.
  // Внутри группы порядок сохраняется (бэк отдаёт sort_order ASC).
  const groups = groupByPriority(items)

  return (
    <div className="rounded-xl border border-border-subtle bg-page-bg/40 p-4">
      <div className="flex items-center gap-2 mb-3 text-xs uppercase tracking-wide text-text-tertiary">
        <Lightbulb size={12} />
        Рекомендации
        <span className="text-text-tertiary normal-case tracking-normal">
          · {items.length}
        </span>
      </div>
      <div className="space-y-5">
        {groups.map((g) => (
          <div key={g.priority}>
            <div className="flex items-center gap-2 mb-2">
              <span
                className={cn(
                  'inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold border',
                  g.meta.badgeClass,
                )}
              >
                {g.meta.label}
              </span>
              <span className="text-[11px] text-text-tertiary">
                {g.items.length}
              </span>
            </div>
            <ul className="space-y-2.5">
              {g.items.map((r) => (
                <RecommendationItem key={r.id} rec={r} />
              ))}
            </ul>
          </div>
        ))}
      </div>
    </div>
  )
}

interface PriorityGroup {
  priority: number
  meta: { label: string; badgeClass: string }
  items: RecommendationDto[]
}

function groupByPriority(items: RecommendationDto[]): PriorityGroup[] {
  return [1, 2, 3]
    .map((priority) => ({
      priority,
      meta: PRIORITY_META[priority],
      items: items.filter((r) => normalizePriority(r.priority) === priority),
    }))
    .filter((g) => g.items.length > 0)
}

// Приоритеты вне 1..3 (на случай дрейфа контракта LLM) сводим к «низкому»,
// чтобы рекомендация не потерялась — RecommendationItem делает то же для бейджа.
function normalizePriority(p: number): 1 | 2 | 3 {
  return p === 1 || p === 2 ? p : 3
}

function RecommendationItem({ rec }: { rec: RecommendationDto }) {
  const [evidenceOpen, setEvidenceOpen] = useState(false)

  // Приоритет вынесен в заголовок группы (groupByPriority), поэтому на карточке
  // оставляем только тему — иначе бейдж дублировался бы в каждой строке секции.
  return (
    <li className="rounded-xl border border-border-subtle bg-card px-4 py-3">
      <div className="flex items-start gap-2 flex-wrap mb-1.5">
        <span className="inline-flex items-center px-2 py-0.5 rounded text-[10px] font-medium bg-slate-100 text-slate-700 border border-slate-200">
          {rec.topic}
        </span>
      </div>
      <div className="text-sm font-semibold text-text-primary mb-1">{rec.title}</div>
      <div className="text-sm text-text-secondary whitespace-pre-line">{rec.body}</div>

      {rec.expectedImpact && (
        <div className="mt-2 flex items-start gap-1.5 text-xs text-text-tertiary">
          <TrendingUp size={11} className="mt-0.5 shrink-0 text-emerald-600" />
          <span>
            <span className="font-medium text-text-secondary">Ожидаемый эффект: </span>
            {rec.expectedImpact}
          </span>
        </div>
      )}

      {rec.evidence.length > 0 && (
        <div className="mt-2.5">
          <button
            type="button"
            onClick={() => setEvidenceOpen((v) => !v)}
            className="inline-flex items-center gap-1 text-[11px] text-text-secondary hover:text-text-primary transition-colors"
            aria-expanded={evidenceOpen}
          >
            <MessageSquareQuote size={11} />
            Примеры цитат из отзывов · {rec.evidence.length}
            <ChevronDown
              size={12}
              className={cn('transition-transform', evidenceOpen && 'rotate-180')}
            />
          </button>
          {evidenceOpen && (
            <ul className="mt-2 space-y-1.5 pl-3 border-l-2 border-border-subtle">
              {rec.evidence.map((quote, i) => (
                <li key={i} className="text-xs text-text-secondary italic">
                  «{quote}»
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </li>
  )
}

const PRIORITY_META: Record<number, { label: string; badgeClass: string }> = {
  1: {
    label: 'Высокий',
    badgeClass: 'bg-rose-100 text-rose-700 border-rose-200',
  },
  2: {
    label: 'Средний',
    badgeClass: 'bg-amber-100 text-amber-700 border-amber-200',
  },
  3: {
    label: 'Низкий',
    badgeClass: 'bg-slate-100 text-slate-600 border-slate-200',
  },
}
