import { http } from './http'

// ----- Types (camelCase, как отдаёт Web API) -----

export type MonitoringStatus = 'active' | 'paused' | 'error'
export type MonitoringCycleStatus = 'running' | 'success' | 'partial' | 'failed'
export type MonitoringFrequency =
  | 'Every10Min'
  | 'Every30Min'
  | 'Daily'
  | 'Weekly'
  | 'Biweekly'
  | 'Monthly'

export interface MonitoringBranch {
  id: string
  name: string | null
  city: string | null
}

export interface MonitoringListItem {
  id: string
  companyId: string
  companyName: string
  seedJobId: string
  sources: string[]
  branches: MonitoringBranch[]
  windowDays: number
  frequency: MonitoringFrequency
  status: MonitoringStatus
  lastCollectedAt: string | null
  lastRunStatus: MonitoringCycleStatus | null
  createdAt: string
}

export interface RecommendationSnapshot {
  priority: number
  topic: string
  title: string
  body: string
  expectedImpact: string | null
  evidence: string[]
}

export interface MonitoringCycle {
  cycleNumber: number
  startedAt: string
  finishedAt: string | null
  status: MonitoringCycleStatus
  periodFrom: string | null
  periodTo: string
  newReviewCount: number
  totalReviewsAtCycle: number
  negativeRatioPp: number
  negativeSpikeTriggered: boolean
  summary: string | null
  recommendations: RecommendationSnapshot[]
  error: string | null
}

export interface MonitoringDetail {
  monitoring: MonitoringListItem
  cycles: MonitoringCycle[]
}

export interface CreateMonitoringRequest {
  companyId: string
  seedJobId: string
  sources: string[]
  branchIds: string[]
  windowDays: number
  frequency: MonitoringFrequency
}

export interface UpdateMonitoringRequest {
  sources: string[]
  branchIds: string[]
  windowDays: number
  frequency: MonitoringFrequency
}

// ----- Option metadata (label + доступность по роли) -----

export const WINDOW_OPTIONS: { value: number; label: string }[] = [
  { value: 7, label: 'Последние 7 дней' },
  { value: 30, label: 'Последние 30 дней' },
  { value: 90, label: 'Последние 90 дней' },
]

export const FREQUENCY_LABEL: Record<MonitoringFrequency, string> = {
  Every10Min: 'Каждые 10 минут',
  Every30Min: 'Каждые 30 минут',
  Daily: 'Раз в сутки',
  Weekly: 'Раз в неделю',
  Biweekly: 'Раз в 2 недели',
  Monthly: 'Раз в месяц',
}

// Наборы частот по роли (синхронно с backend MonitoringFrequencies).
export const ADMIN_FREQUENCIES: MonitoringFrequency[] = ['Every10Min', 'Every30Min', 'Daily']
export const USER_FREQUENCIES: MonitoringFrequency[] = ['Daily', 'Weekly', 'Biweekly', 'Monthly']

export function frequenciesForRole(isAdmin: boolean): MonitoringFrequency[] {
  return isAdmin ? ADMIN_FREQUENCIES : USER_FREQUENCIES
}

export const MONITORING_STATUS_LABEL: Record<MonitoringStatus, string> = {
  active: 'Активен',
  paused: 'Пауза',
  error: 'Ошибка',
}

// ----- API -----

export const monitoringsApi = {
  list: () =>
    http.get<{ items: MonitoringListItem[] }>('/api/monitorings').then((r) => r.data.items),

  get: (id: string) =>
    http.get<MonitoringDetail>(`/api/monitorings/${id}`).then((r) => r.data),

  create: (request: CreateMonitoringRequest) =>
    http.post<{ id: string }>('/api/monitorings', request).then((r) => r.data),

  update: (id: string, request: UpdateMonitoringRequest) =>
    http.put<MonitoringListItem>(`/api/monitorings/${id}`, request).then((r) => r.data),

  pause: (id: string) => http.post(`/api/monitorings/${id}/pause`).then(() => undefined),
  resume: (id: string) => http.post(`/api/monitorings/${id}/resume`).then(() => undefined),
  run: (id: string) => http.post(`/api/monitorings/${id}/run`).then(() => undefined),
  remove: (id: string) => http.delete(`/api/monitorings/${id}`).then(() => undefined),
}
