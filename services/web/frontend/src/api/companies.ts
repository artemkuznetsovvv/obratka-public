import { http } from './http'

export interface CompanyDto {
  id: string
  name: string
  category: string | null
  subcategory: string | null
  cities: string[]
  description: string | null
  branchCount: number
  createdAt: string
  updatedAt: string
}

export interface CompanyBranchDto {
  id: string
  source: string
  externalId: string | null
  externalUrl: string | null
  name: string
  address: string | null
  city: string
  rating: number | null
  reviewCount: number | null
}

export interface CreateCompanyRequest {
  name: string
  category?: string | null
  subcategory?: string | null
  cities: string[]
  description?: string | null
}

export interface BranchSearchResultItem {
  id: string
  source: string
  externalId: string | null
  externalUrl: string | null
  name: string
  address: string | null
  rating: number | null
  // reviewCount = «число оценок» рядом с рейтингом (для 2GIS/Yandex — rating votes,
  // НЕ отзывы; для Google совпадает с realReviewsCount).
  reviewCount: number | null
  // realReviewsCount = настоящее число отзывов с текстом. null если источник
  // не отдаёт точное значение (Yandex multi-result search list).
  realReviewsCount: number | null
}

export interface BranchSearchSourceGroup {
  source: string
  items: BranchSearchResultItem[]
}

export interface BranchSearchResponse {
  city: string
  sources: BranchSearchSourceGroup[]
}

export const companiesApi = {
  create: (request: CreateCompanyRequest) =>
    http.post<CompanyDto>('/api/companies', request).then((r) => r.data),

  update: (id: string, request: CreateCompanyRequest) =>
    http.put<CompanyDto>(`/api/companies/${id}`, request).then((r) => r.data),

  get: (id: string) => http.get<CompanyDto>(`/api/companies/${id}`).then((r) => r.data),

  search: (id: string, city: string, sources?: string[]) =>
    http
      .post<BranchSearchResponse>(`/api/companies/${id}/search`, null, {
        params: { city, sources },
        paramsSerializer: { indexes: null },
      })
      .then((r) => r.data),

  saveBranches: (id: string, branchIds: string[]) =>
    http.post<CompanyBranchDto[]>(`/api/companies/${id}/branches`, { branchIds }).then((r) => r.data),

  listBranches: (id: string) =>
    http.get<CompanyBranchDto[]>(`/api/companies/${id}/branches`).then((r) => r.data),
}
