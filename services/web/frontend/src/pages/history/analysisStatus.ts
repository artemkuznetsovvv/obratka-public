// Утилиты для отображения статусов AnalysisJob — переиспользуются на /history (список)
// и /history/:jobId (деталь). FSM в PG: pending → collecting → sent_to_llm →
// computing_aggregates → completed | partial | failed.

export type AnalysisStatus =
  | 'pending'
  | 'collecting'
  | 'sent_to_llm'
  | 'computing_aggregates'
  | 'completed'
  | 'partial'
  | 'failed'
  | string

export const TERMINAL_STATUSES = new Set<string>(['completed', 'partial', 'failed'])

export const isTerminal = (status: string) => TERMINAL_STATUSES.has(status)

export interface StatusMeta {
  label: string
  // tailwind utility classes for badge: bg, text, border
  badge: string
  // for progress / state hints
  tone: 'pending' | 'progress' | 'ok' | 'warn' | 'error'
}

export const STATUS_META: Record<string, StatusMeta> = {
  pending: {
    label: 'Ожидание',
    badge: 'bg-slate-100 text-slate-700 border-slate-200',
    tone: 'pending',
  },
  collecting: {
    label: 'Сбор отзывов',
    badge: 'bg-blue-50 text-blue-700 border-blue-200',
    tone: 'progress',
  },
  sent_to_llm: {
    label: 'Анализ AI',
    badge: 'bg-violet-50 text-violet-700 border-violet-200',
    tone: 'progress',
  },
  computing_aggregates: {
    label: 'Расчёт метрик',
    badge: 'bg-violet-50 text-violet-700 border-violet-200',
    tone: 'progress',
  },
  completed: {
    label: 'Завершён',
    badge: 'bg-emerald-50 text-emerald-700 border-emerald-200',
    tone: 'ok',
  },
  partial: {
    label: 'Частично',
    badge: 'bg-amber-50 text-amber-700 border-amber-200',
    tone: 'warn',
  },
  failed: {
    label: 'Ошибка',
    badge: 'bg-rose-50 text-rose-700 border-rose-200',
    tone: 'error',
  },
}

export function statusMetaFor(status: string): StatusMeta {
  return STATUS_META[status] ?? {
    label: status,
    badge: 'bg-slate-100 text-slate-700 border-slate-200',
    tone: 'pending',
  }
}

// Грубая оценка прогресса по стадиям FSM для UI прогресс-бара. Возвращает 0..100.
// pending=5, collecting=25-60 (внутри уточняется из collectionProgress если есть),
// sent_to_llm=70, computing_aggregates=90, completed=100, failed=100.
export function approximateProgress(
  status: string,
  collectionProgress?: Record<string, { progress: number }>,
): number {
  switch (status) {
    case 'pending':
      return 5
    case 'collecting': {
      if (!collectionProgress || Object.keys(collectionProgress).length === 0) return 20
      const values = Object.values(collectionProgress).map((c) => c.progress ?? 0)
      // 0..1 на источник → среднее * 35 + 25 (диапазон collecting на нашей шкале: 25..60).
      const avg = values.reduce((a, b) => a + b, 0) / values.length
      return Math.round(25 + avg * 35)
    }
    case 'sent_to_llm':
      return 70
    case 'computing_aggregates':
      return 90
    case 'completed':
    case 'partial':
    case 'failed':
      return 100
    default:
      return 10
  }
}

export const SOURCE_LABEL: Record<string, string> = {
  '2gis': '2ГИС',
  yandex: 'Яндекс.Карты',
  google: 'Google Maps',
}
