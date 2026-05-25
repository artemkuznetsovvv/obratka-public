import { http } from './http'

// Шапка дашборда. Метрики (О1-О3, 1-7) пока не возвращаются — добавятся
// отдельными полями по мере реализации.
export interface DashboardBranchDto {
  branchId: string
  name: string | null
  address: string | null
}

export interface DashboardHeaderDto {
  jobId: string
  companyId: string
  companyName: string
  branches: DashboardBranchDto[]
  sources: string[]
  status: string
  reviewCount: number
  recommendationsCount: number
  createdAt: string
  completedAt: string | null
  // Caveat: фактический период джоба в processing_db не хранится — backend
  // возвращает draftPeriodFrom/To из Company как fallback. Может расходиться
  // с реально проанализированным периодом, если юзер перенастроил после запуска.
  periodFrom: string | null
  periodTo: string | null
}

export const dashboardsApi = {
  get: (jobId: string) =>
    http
      .get<DashboardHeaderDto>(`/api/analyses/${jobId}/dashboard`)
      .then((r) => r.data),
}
