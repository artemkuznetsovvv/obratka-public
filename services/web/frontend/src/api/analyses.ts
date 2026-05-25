import { http } from './http'
import type { AnalysisJob, AnalysisJobListResponse } from './admin'

export type { AnalysisJob, AnalysisJobListResponse, CollectionProgressEntry } from './admin'

export interface StartAnalysisRequest {
  companyId: string
  // ISO 8601 — backend ожидает DateTimeOffset?. null/undefined = «с самого начала».
  periodFrom?: string | null
  periodTo?: string | null
}

export interface StartAnalysisResponse {
  analysisJobId: string
}

export interface BranchStatsDto {
  branchId: string
  branchName: string | null
  branchAddress: string | null
  source: string
  reviewCount: number
}

export const analysesApi = {
  start: (request: StartAnalysisRequest) =>
    http.post<StartAnalysisResponse>('/api/analyses/start', request).then((r) => r.data),

  list: (params?: { status?: string; companyId?: string; limit?: number; offset?: number }) =>
    http.get<AnalysisJobListResponse>('/api/analyses', { params }).then((r) => r.data),

  get: (jobId: string) =>
    http.get<AnalysisJob>(`/api/analyses/${jobId}`).then((r) => r.data),

  branchStats: (jobId: string) =>
    http.get<BranchStatsDto[]>(`/api/analyses/${jobId}/branch-stats`).then((r) => r.data),
}
