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
  expiresAt: string | null
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
  expiresAt?: string | null
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

  setExpiresAt: (id: number, expiresAt: string | null) =>
    http
      .post<ParserProxy>(`/api/admin/proxies/${id}/set-expires-at`, { expiresAt })
      .then((r) => r.data),
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

export interface StartAnalysisRequest {
  companyId: string
  dateFrom?: string | null
  dateTo?: string | null
}

export interface StartAnalysisResponse {
  analysisJobId: string
}

export interface RestartSourceRequest {
  dateFrom?: string | null
  dateTo?: string | null
}

export interface RestartSourceResponse {
  source: string
  taskId: string
  previousStatus: string
  currentStatus: string
}

export interface JobBlobItem {
  key: string
  size: number
  lastModified: string
}

export interface JobBlobList {
  bucket: string
  prefix: string
  count: number
  items: JobBlobItem[]
}

export const adminAnalysesApi = {
  list: (params?: { status?: string; companyId?: string; limit?: number; offset?: number }) =>
    http.get<AnalysisJobListResponse>('/api/admin/analyses', { params }).then((r) => r.data),

  get: (jobId: string) =>
    http.get<AnalysisJob>(`/api/admin/analyses/${jobId}`).then((r) => r.data),

  start: (request: StartAnalysisRequest) =>
    http.post<StartAnalysisResponse>('/api/admin/analyses', request).then((r) => r.data),

  restartSource: (jobId: string, source: string, request: RestartSourceRequest) =>
    http
      .post<RestartSourceResponse>(`/api/admin/analyses/${jobId}/restart-source/${source}`, request)
      .then((r) => r.data),

  llmReplay: (jobId: string) =>
    http.post<void>(`/api/admin/analyses/${jobId}/llm-replay`).then((r) => r.data),

  listBlobs: (jobId: string) =>
    http.get<JobBlobList>(`/api/admin/analyses/${jobId}/blobs`).then((r) => r.data),

  // Returns a Blob + suggested filename. Body is streamed through Web API, so JWT auth applies.
  downloadBlob: async (jobId: string, name: string) => {
    const response = await http.get<Blob>(`/api/admin/analyses/${jobId}/blobs/${name}`, {
      responseType: 'blob',
    })
    const disposition = response.headers['content-disposition'] as string | undefined
    const fileName = parseFileName(disposition) ?? `${jobId}-${name.replace('/', '-')}.json`
    return { blob: response.data, fileName }
  },
}

function parseFileName(disposition: string | undefined): string | null {
  if (!disposition) return null
  // matches filename*=UTF-8''...  OR  filename="..."  OR  filename=...
  const utf8 = /filename\*=UTF-8''([^;]+)/i.exec(disposition)
  if (utf8) return decodeURIComponent(utf8[1])
  const quoted = /filename="([^"]+)"/i.exec(disposition)
  if (quoted) return quoted[1]
  const bare = /filename=([^;]+)/i.exec(disposition)
  return bare ? bare[1].trim() : null
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
