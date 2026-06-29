import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios'
import { tokenStore } from '@/auth/token-store'

type RetriableConfig = InternalAxiosRequestConfig & { _retry?: boolean }

export const http = axios.create({
  baseURL: '',
  headers: { 'Content-Type': 'application/json' },
})

http.interceptors.request.use((config) => {
  const token = tokenStore.get()
  if (token && !config.headers.Authorization) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

http.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const original = error.config as RetriableConfig | undefined
    const status = error.response?.status

    // Refresh once on 401 for non-auth endpoints
    if (
      status === 401 &&
      original &&
      !original._retry &&
      !(original.url ?? '').includes('/api/auth/')
    ) {
      original._retry = true
      try {
        const { data } = await axios.post<{ accessToken: string }>(
          '/api/auth/refresh',
          {},
        )
        tokenStore.set(data.accessToken)
        original.headers.Authorization = `Bearer ${data.accessToken}`
        return http(original)
      } catch {
        tokenStore.set(null)
        tokenStore.triggerLogout()
      }
    }

    return Promise.reject(error)
  },
)
