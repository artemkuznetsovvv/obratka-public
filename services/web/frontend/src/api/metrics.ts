import { http } from './http'

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
  // CSV: 'позитивный,нейтральный,негативный'. null/empty = не фильтровать.
  sentiments: string[]
  // CSV: '1,2,3'. null/empty = не фильтровать.
  stars: number[]
}

export const metricsApi = {
  reviewCount: (jobId: string, q: ReviewCountQuery) =>
    http
      .get<ReviewCountMetricDto>(`/api/analyses/${jobId}/metrics/review-count`, {
        params: {
          branchIds: q.branchIds.join(','),
          from: q.from ?? undefined,
          to: q.to ?? undefined,
          // Не передаём параметр вообще, если массив пустой — бэк трактует как «фильтр снят».
          // Это сделано чтобы пустой select на UI не сворачивал данные в ноль.
          sentiments: q.sentiments.length > 0 ? q.sentiments.join(',') : undefined,
          stars: q.stars.length > 0 ? q.stars.join(',') : undefined,
        },
      })
      .then((r) => r.data),
}
