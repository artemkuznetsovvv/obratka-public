import { http } from './http'
import {
  ALL_SENTIMENTS,
  ALL_STARS,
} from '@/pages/dashboards/DashboardFiltersContext'

// Метрика 1: «Количество отзывов». bySource всегда содержит ровно 3 элемента
// (2gis / yandex / google), даже если по какому-то источнику 0 отзывов.
// hasPreviousPeriod=false когда period не задан с обеих сторон — UI рисует «—».
export interface ReviewCountSourceDto {
  source: string
  current: number
  previous: number
}

export interface ReviewCountMetricDto {
  totalCurrent: number
  totalPrevious: number
  hasPreviousPeriod: boolean
  bySource: ReviewCountSourceDto[]
}

export interface ReviewCountQuery {
  // Непустой массив branchIds. М1 (per-branch) → 1 элемент, О1 (по сети) → N.
  branchIds: string[]
  // ISO 8601 datetime или null. Бэк принимает DateTimeOffset?.
  from: string | null
  to: string | null
  // null/empty или «все опции» = не фильтровать (см. ниже).
  sentiments: string[]
  stars: number[]
}

// Метрика 2: «Средний рейтинг». Те же параметры что у М1.
// average/totalAverage — null если 0 отзывов со stars в срезе.
export interface AverageRatingSourceDto {
  source: string
  average: number | null
  count: number
}

export interface AverageRatingMetricDto {
  totalAverage: number | null
  totalCount: number
  bySource: AverageRatingSourceDto[]
}

// Параметры идентичны ReviewCountQuery — переиспользуем тип.
export type AverageRatingQuery = ReviewCountQuery

// Метрика 3 «Настроение клиентов». Counts по 3 sentiment-bucket'ам;
// totalNonEmpty=0 → empty state «Нет данных».
export interface SentimentDistributionMetricDto {
  positive: number
  neutral: number
  negative: number
  totalNonEmpty: number
}

// М3/О3 принимает SOURCES (а не sentiments — она сама про sentiments).
export interface SentimentDistributionQuery {
  branchIds: string[]
  from: string | null
  to: string | null
  sources: string[]
  stars: number[]
}

// Список отзывов конкретной тональности для модалки раскрытия М3/О3.
// Сортировка ReviewDate DESC, limit/offset для постраничной подгрузки.
export interface SentimentReviewItemDto {
  id: number
  source: string
  reviewDate: string
  stars: number | null
  text: string
}

export interface SentimentReviewsDto {
  items: SentimentReviewItemDto[]
  hasMore: boolean
}

export interface SentimentReviewsQuery extends SentimentDistributionQuery {
  sentiment: 'позитивный' | 'нейтральный' | 'негативный'
  limit?: number
  offset?: number
}

// Метрика 4 «Свежий пульс». Окно жёстко 30 дней — period на UI не выбирается.
// index ∈ [-100, +100] либо null если нет отзывов в окне.
export interface FreshPulseWindowDto {
  index: number | null
  positive: number
  neutral: number
  negative: number
  totalNonEmpty: number
  fromInclusive: string
  toExclusive: string
}

export interface FreshPulseMetricDto {
  current: FreshPulseWindowDto
  previous: FreshPulseWindowDto
}

// М4 принимает branch+sources+stars; sentiments и period НЕ передаются.
export interface FreshPulseQuery {
  branchIds: string[]
  sources: string[]
  stars: number[]
}

// Метрика 5 «О чём говорят чаще всего» — топ-3 темы.
// totalReviewsInPeriod нужен фронту для расчёта доли темы (reviewCount / total).
export interface TopicAggregateDto {
  topic: string
  reviewCount: number
  positiveMentions: number
  negativeMentions: number
}

export interface TopTopicsMetricDto {
  topics: TopicAggregateDto[]
  totalReviewsInPeriod: number
}

// М5: branch + period + sources + stars (sentiments не передаётся — карточка
// сама показывает разрез по тональности).
export type TopTopicsQuery = SentimentDistributionQuery

// Метрика 6 «Сколько клиентов рекомендуют». Counts → фронт считает %.
// hasPreviousPeriod=false когда фильтр периода не задан полностью.
export interface RecommendPercentWindowDto {
  positive: number
  totalNonEmpty: number
}

export interface RecommendPercentMetricDto {
  current: RecommendPercentWindowDto
  previous: RecommendPercentWindowDto
  hasPreviousPeriod: boolean
}

// М6: те же параметры что М5 (без sentiments).
export type RecommendPercentQuery = SentimentDistributionQuery

// Метрика 7 «Новые отзывы за период». Counts current+3 prev окон того же
// размера + fullPreviousWindows ∈ {0..3} — индикатор «есть ли история».
// Фронт сам решает режим (полный vs облегчённый) по правилу fullPrev ≥ 2.
export type RecentReviewsWindow = '7d' | '30d' | '3m' | '6m' | '12m'

export interface RecentReviewsMetricDto {
  window: string
  currentCount: number
  prev1Count: number      // самое свежее prev-окно (right next to current)
  prev2Count: number
  prev3Count: number      // самое старое prev-окно
  fullPreviousWindows: number
  currentFromInclusive: string
  currentToExclusive: string
}

// М7: window выбирается переключателем на самой карточке (НЕ из контекста).
// period и sentiments НЕ передаются.
export interface RecentReviewsQuery {
  branchIds: string[]
  window: RecentReviewsWindow
  sources: string[]
  stars: number[]
}

export const metricsApi = {
  reviewCount: (jobId: string, q: ReviewCountQuery) =>
    http
      .get<ReviewCountMetricDto>(`/api/analyses/${jobId}/metrics/review-count`, {
        params: buildParams(q),
      })
      .then((r) => r.data),

  averageRating: (jobId: string, q: AverageRatingQuery) =>
    http
      .get<AverageRatingMetricDto>(`/api/analyses/${jobId}/metrics/average-rating`, {
        params: buildParams(q),
      })
      .then((r) => r.data),

  sentimentDistribution: (jobId: string, q: SentimentDistributionQuery) =>
    http
      .get<SentimentDistributionMetricDto>(
        `/api/analyses/${jobId}/metrics/sentiment-distribution`,
        { params: buildSentimentParams(q) },
      )
      .then((r) => r.data),

  topTopics: (jobId: string, q: TopTopicsQuery) =>
    http
      .get<TopTopicsMetricDto>(`/api/analyses/${jobId}/metrics/top-topics`, {
        params: buildSentimentParams(q),
      })
      .then((r) => r.data),

  recentReviews: (jobId: string, q: RecentReviewsQuery) =>
    http
      .get<RecentReviewsMetricDto>(`/api/analyses/${jobId}/metrics/recent-reviews`, {
        params: {
          branchIds: q.branchIds.join(','),
          window: q.window,
          sources: shouldSendFilter(q.sources, 3) ? q.sources.join(',') : undefined,
          stars: shouldSendFilter(q.stars, ALL_STARS.length)
            ? q.stars.join(',')
            : undefined,
        },
      })
      .then((r) => r.data),

  recommendPercent: (jobId: string, q: RecommendPercentQuery) =>
    http
      .get<RecommendPercentMetricDto>(
        `/api/analyses/${jobId}/metrics/recommend-percent`,
        { params: buildSentimentParams(q) },
      )
      .then((r) => r.data),

  freshPulse: (jobId: string, q: FreshPulseQuery) =>
    http
      .get<FreshPulseMetricDto>(`/api/analyses/${jobId}/metrics/fresh-pulse`, {
        params: {
          branchIds: q.branchIds.join(','),
          sources: shouldSendFilter(q.sources, 3) ? q.sources.join(',') : undefined,
          stars: shouldSendFilter(q.stars, ALL_STARS.length)
            ? q.stars.join(',')
            : undefined,
        },
      })
      .then((r) => r.data),

  sentimentReviews: (jobId: string, q: SentimentReviewsQuery) =>
    http
      .get<SentimentReviewsDto>(
        `/api/analyses/${jobId}/metrics/sentiment-reviews`,
        {
          params: {
            ...buildSentimentParams(q),
            sentiment: q.sentiment,
            limit: q.limit,
            offset: q.offset,
          },
        },
      )
      .then((r) => r.data),
}

// Параметры sentiment-distribution и sentiment-reviews идентичны — выносим в helper.
function buildSentimentParams(q: SentimentDistributionQuery) {
  return {
    branchIds: q.branchIds.join(','),
    from: q.from ?? undefined,
    to: q.to ?? undefined,
    // sources фильтрует на бэке (в отличие от М1/М2 где фильтр идёт UI-side).
    // Правило «все = не фильтрую» — то же, для consistency с другими фильтрами.
    sources: shouldSendFilter(q.sources, 3) ? q.sources.join(',') : undefined,
    stars: shouldSendFilter(q.stars, ALL_STARS.length)
      ? q.stars.join(',')
      : undefined,
  }
}

// Один и тот же набор query-параметров переиспользуется всеми /metrics/*
// (фильтры дашборда едины). Выносим в helper.
function buildParams(q: ReviewCountQuery) {
  return {
    branchIds: q.branchIds.join(','),
    from: q.from ?? undefined,
    to: q.to ?? undefined,
    // «Все опции» трактуем как «фильтр не применять» — НЕ передаём параметр.
    // Иначе бэк делает INNER JOIN на review_llm_results (для sentiments) и
    // исключает отзывы без LLM-результата; для stars — исключает с NULL.
    sentiments: shouldSendFilter(q.sentiments, ALL_SENTIMENTS.length)
      ? q.sentiments.join(',')
      : undefined,
    stars: shouldSendFilter(q.stars, ALL_STARS.length)
      ? q.stars.join(',')
      : undefined,
  }
}

// Передаём фильтр только если выбран неполный поднабор (1..N-1 опций).
// Пусто (0) или «все» (N) → не передаём.
function shouldSendFilter(selected: readonly unknown[], totalOptions: number): boolean {
  return selected.length > 0 && selected.length < totalOptions
}
