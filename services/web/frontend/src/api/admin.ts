import { http } from './http'

// ----- Users -----
export interface AdminUserListItem {
  id: string
  email: string
  fullName: string
  isBlocked: boolean
  roles: string[]
  createdAt: string
}

export interface AdminUserListResponse {
  total: number
  items: AdminUserListItem[]
}

export const adminUsersApi = {
  list: (params?: { limit?: number; offset?: number }) =>
    http.get<AdminUserListResponse>('/api/admin/users', { params }).then((r) => r.data),

  block: (id: string) =>
    http.post<AdminUserListItem>(`/api/admin/users/${id}/block`).then((r) => r.data),

  unblock: (id: string) =>
    http.post<AdminUserListItem>(`/api/admin/users/${id}/unblock`).then((r) => r.data),
}

// ----- Proxies (mirror Parser-Service DTO) -----
export interface ParserProxy {
  id: number
  host: string
  port: number
  protocol: string
  username: string | null
  enabled: boolean
  failureCount: number
  cooldownUntil: string | null
  lastUsedAt: string | null
  notes: string | null
  createdAt: string
  updatedAt: string
}

export interface ParserProxyListResponse {
  total: number
  items: ParserProxy[]
}

export interface CreateParserProxyRequest {
  host: string
  port: number
  protocol: string
  username?: string | null
  password?: string | null
  notes?: string | null
  enabled?: boolean | null
}

export const adminProxiesApi = {
  list: (enabledOnly?: boolean) =>
    http
      .get<ParserProxyListResponse>('/api/admin/proxies', {
        params: enabledOnly !== undefined ? { enabledOnly } : undefined,
      })
      .then((r) => r.data),

  create: (request: CreateParserProxyRequest) =>
    http.post<ParserProxy>('/api/admin/proxies', request).then((r) => r.data),

  delete: (id: number) => http.delete<void>(`/api/admin/proxies/${id}`).then((r) => r.data),

  disable: (id: number) =>
    http.post<ParserProxy>(`/api/admin/proxies/${id}/disable`).then((r) => r.data),

  enable: (id: number) =>
    http.post<ParserProxy>(`/api/admin/proxies/${id}/enable`).then((r) => r.data),

  resetHealth: (id: number) =>
    http.post<ParserProxy>(`/api/admin/proxies/${id}/reset-health`).then((r) => r.data),
}

// ----- Parser tasks -----
export interface ParserTask {
  taskId: string
  jobId: string
  companyId: string
  source: string
  status: string
  progress: number
  reviewCount: number | null
  s3Url: string | null
  error: string | null
  createdAt: string
  updatedAt: string
}

export interface ParserTaskListResponse {
  count: number
  limit: number
  offset: number
  items: ParserTask[]
}

export const adminParserTasksApi = {
  list: (params?: { status?: string; source?: string; limit?: number; offset?: number }) =>
    http.get<ParserTaskListResponse>('/api/admin/parser-tasks', { params }).then((r) => r.data),

  get: (taskId: string) =>
    http.get<ParserTask>(`/api/admin/parser-tasks/${taskId}`).then((r) => r.data),
}

// ----- Analyses (Processing-Gateway via Web API) -----
export interface CollectionProgressEntry {
  taskId: string
  status: string
  progress: number
  reviewCount: number | null
  s3Url: string | null
  error: string | null
}

export interface AnalysisJob {
  id: string
  companyId: string
  status: string
  reviewCount: number
  collectionProgress: Record<string, CollectionProgressEntry>
  payloadUrl: string | null
  resultReviewsUrl: string | null
  resultSummaryUrl: string | null
  summary: string | null
  recommendationsCount: number
  createdAt: string
  sentAt: string | null
  completedAt: string | null
  error: string | null
}

export interface AnalysisJobListResponse {
  total: number
  limit: number
  offset: number
  items: AnalysisJob[]
}

export const adminAnalysesApi = {
  list: (params?: { status?: string; companyId?: string; limit?: number; offset?: number }) =>
    http.get<AnalysisJobListResponse>('/api/admin/analyses', { params }).then((r) => r.data),

  get: (jobId: string) =>
    http.get<AnalysisJob>(`/api/admin/analyses/${jobId}`).then((r) => r.data),
}

// ----- Companies (admin registry) -----
export interface AdminCompanyListItem {
  id: string
  name: string
  category: string | null
  subcategory: string | null
  cities: string[]
  branchCount: number
  selectedBranchCount: number
  ownerUserId: string
  ownerEmail: string
  ownerFullName: string
  createdAt: string
  updatedAt: string
}

export interface AdminCompanyListResponse {
  total: number
  limit: number
  offset: number
  items: AdminCompanyListItem[]
}

export interface AdminCompanyBranchDto {
  id: string
  source: string
  externalId: string
  externalUrl: string
  name: string
  address: string | null
  city: string
  rating: number | null
  reviewCount: number | null
  isSelected: boolean
  createdAt: string
}

export interface AdminCompanyDetails {
  id: string
  name: string
  category: string | null
  subcategory: string | null
  cities: string[]
  description: string | null
  ownerUserId: string
  ownerEmail: string
  ownerFullName: string
  createdAt: string
  updatedAt: string
  branches: AdminCompanyBranchDto[]
}

export const adminCompaniesApi = {
  list: (params?: { limit?: number; offset?: number; search?: string }) =>
    http.get<AdminCompanyListResponse>('/api/admin/companies', { params }).then((r) => r.data),

  get: (id: string) =>
    http.get<AdminCompanyDetails>(`/api/admin/companies/${id}`).then((r) => r.data),
}
