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

// Эвристика общего прогресса по стадиям FSM для большого круга/линейного бара.
// Возвращает 0..100. ВАЖНО: collectionProgress[source].progress приходит из PG как
// int 0..100 (см. RawCollectionEntry.Progress) — не 0..1, не процент конкретного branch'а.
//
// Распределение «бюджета» прогресса по стадиям:
//   pending                =   0..5  (заявка отправлена, ничего не делается)
//   collecting             =   5..40 (зависит от avg sources)
//   sent_to_llm            =  40..80 (LLM сам не сообщает % — линейная аппроксимация по времени мы НЕ делаем,
//                                     просто скакаем в 60 как «середина LLM-работы»)
//   computing_aggregates   =  80..95 (Analytics-модуль агрегирует)
//   completed / partial    = 100
//   failed                 = текущее значение, но красным
export function approximateProgress(
  status: string,
  collectionProgress?: Record<string, { progress: number }>,
): number {
  switch (status) {
    case 'pending':
      return 5
    case 'collecting': {
      if (!collectionProgress || Object.keys(collectionProgress).length === 0) return 10
      const values = Object.values(collectionProgress).map((c) => c.progress ?? 0)
      // PG отдаёт progress per source как int 0..100. Берём среднее и масштабируем
      // в наш «бюджет» сбора 5..40 → коэф 0.35, базовый отступ 5.
      const avg = values.reduce((a, b) => a + b, 0) / values.length
      return Math.round(5 + (avg / 100) * 35)
    }
    case 'sent_to_llm':
      return 60
    case 'computing_aggregates':
      return 90
    case 'completed':
    case 'partial':
      return 100
    case 'failed':
      // Падение на любом этапе — бар показывает где упало (рендер красным сверху).
      // Если есть данные сбора, оценим там, иначе 50% как «где-то посередине».
      if (collectionProgress && Object.keys(collectionProgress).length > 0) {
        const values = Object.values(collectionProgress).map((c) => c.progress ?? 0)
        const avg = values.reduce((a, b) => a + b, 0) / values.length
        return Math.max(10, Math.round(5 + (avg / 100) * 35))
      }
      return 50
    default:
      return 10
  }
}

export const SOURCE_LABEL: Record<string, string> = {
  '2gis': '2ГИС',
  yandex: 'Яндекс.Карты',
  google: 'Google Maps',
}

// ---- Pipeline stages (для центральной карточки на /history/:jobId) ----
//
// Backend FSM имеет 5 значений (pending → collecting → sent_to_llm →
// computing_aggregates → completed | partial | failed). Дизайн _4 показывает 5
// именованных стадий пайплайна:
//
//   1. Сбор отзывов            ← collecting
//   2. Определение тональности ← sent_to_llm (LLM делает 2-4 одним рывком)
//   3. Тематическая разметка   ← sent_to_llm
//   4. Выявление болевых точек ← sent_to_llm
//   5. Генерация рекомендаций  ← computing_aggregates (формирование итогового отчёта)
//
// LLM на самом деле возвращает overall_sentiment + aspects + summary + recommendations
// в одном проходе — мы не различаем стадии 2/3/4 внутри. Поэтому когда sent_to_llm
// активен — все три стадии (2, 3, 4) одновременно в состоянии «active», когда LLM
// вернул ответ и PG переключился в computing_aggregates — они становятся done и
// активной становится 5.

export type StageState = 'done' | 'active' | 'pending' | 'failed'

export interface PipelineStage {
  key: 'collect' | 'sentiment' | 'topics' | 'pain' | 'recommendations'
  label: string
  state: StageState
}

export function buildPipelineStages(status: string): PipelineStage[] {
  // Defaults — все pending.
  const base: PipelineStage[] = [
    { key: 'collect', label: 'Сбор отзывов', state: 'pending' },
    { key: 'sentiment', label: 'Определение тональности', state: 'pending' },
    { key: 'topics', label: 'Тематическая разметка', state: 'pending' },
    { key: 'pain', label: 'Выявление болевых точек', state: 'pending' },
    { key: 'recommendations', label: 'Генерация рекомендаций', state: 'pending' },
  ]

  switch (status) {
    case 'pending':
      // Job создан, ничего не запускалось — все pending.
      return base
    case 'collecting':
      base[0].state = 'active'
      return base
    case 'sent_to_llm':
      base[0].state = 'done'
      // LLM делает 2-4 одним рывком — анимируем как одновременно активные.
      base[1].state = 'active'
      base[2].state = 'active'
      base[3].state = 'active'
      return base
    case 'computing_aggregates':
      base[0].state = 'done'
      base[1].state = 'done'
      base[2].state = 'done'
      base[3].state = 'done'
      base[4].state = 'active'
      return base
    case 'completed':
      return base.map((s) => ({ ...s, state: 'done' as StageState }))
    case 'partial':
      // Часть источников упала, но pipeline прошёл — отмечаем все как done,
      // а warning тон даёт верхняя плашка статуса.
      return base.map((s) => ({ ...s, state: 'done' as StageState }))
    case 'failed':
      // Падение — текущая активная стадия в failed, остальные в зависимости от FSM-снапшота
      // мы точно не знаем где упало, без collection_progress нельзя сказать. По умолчанию
      // на первой стадии — её и помечаем; остальные pending.
      base[0].state = 'failed'
      return base
    default:
      return base
  }
}

// ---- Top-level stepper (4 шага по дизайну _4) ----
//
// «Параметры» / «Источники» / «Обработка» / «Результаты». На /history/:jobId
// первые два всегда done (анализ уже запущен), 3-й = текущая обработка, 4-й = успех/ждёт.

export type StepperState = 'done' | 'active' | 'pending' | 'failed'

export interface StepperStep {
  index: number
  label: string
  state: StepperState
}

export function buildDetailStepper(status: string): StepperStep[] {
  // Step 3 «Обработка» зависит от FSM
  let processing: StepperState
  let results: StepperState
  switch (status) {
    case 'pending':
    case 'collecting':
    case 'sent_to_llm':
    case 'computing_aggregates':
      processing = 'active'
      results = 'pending'
      break
    case 'completed':
    case 'partial':
      processing = 'done'
      results = 'active' // когда сделаем дашборд — станет «done» при перезоде
      break
    case 'failed':
      processing = 'failed'
      results = 'pending'
      break
    default:
      processing = 'pending'
      results = 'pending'
  }
  return [
    { index: 1, label: 'Параметры', state: 'done' },
    { index: 2, label: 'Источники', state: 'done' },
    { index: 3, label: 'Обработка', state: processing },
    { index: 4, label: 'Результаты', state: results },
  ]
}

// Технические строки job.error / per-source error приходят из Processing Gateway и
// Parser на английском (они же — машинные логи; менять их в PG нельзя — на них завязаны
// тесты/контракты). Для UI переводим известные на русский по префиксу; неизвестные
// (напр. произвольный текст ошибки от LLM) возвращаем как есть, чтобы не потерять детали.
const ANALYSIS_ERROR_RULES: { match: (e: string) => boolean; ru: string }[] = [
  { match: (e) => e === 'All sources failed to collect reviews', ru: 'Не удалось собрать отзывы ни из одного источника' },
  { match: (e) => e.startsWith('All sources failed to start'), ru: 'Не удалось запустить сбор ни по одному источнику' },
  { match: (e) => e === 'No branches supplied', ru: 'Не выбрано ни одного филиала' },
  { match: (e) => e === 'No reviews collected from any source', ru: 'Не собрано ни одного отзыва' },
  { match: (e) => e.startsWith('All collected reviews have empty text'), ru: 'У всех собранных отзывов пустой текст — анализировать нечего' },
  { match: (e) => e.startsWith('Parser task exceeded timeout'), ru: 'Превышено время сбора отзывов' },
  { match: (e) => e.startsWith('Parser task not found'), ru: 'Задача сбора не найдена (потеряна или устарела)' },
]

export function localizeAnalysisError(error: string | null | undefined): string | null {
  if (!error) return null
  const trimmed = error.trim()
  for (const rule of ANALYSIS_ERROR_RULES) {
    if (rule.match(trimmed)) return rule.ru
  }
  return error
}
