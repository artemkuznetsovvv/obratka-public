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
