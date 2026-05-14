import { http } from './http'

export interface CitySuggestion {
  id: number
  name: string
  region: string
}

export interface CitySuggestResponse {
  items: CitySuggestion[]
}

export const citiesApi = {
  suggest: (q: string, limit = 10, signal?: AbortSignal) =>
    http
      .get<CitySuggestResponse>('/api/cities/suggest', {
        params: { q, limit },
        signal,
      })
      .then((r) => r.data),
}
