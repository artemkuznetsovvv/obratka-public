import { http } from './http'

export interface UserInfo {
  id: string
  email: string
  fullName: string
  roles: string[]
}

export interface AuthResponse {
  accessToken: string
  expiresAt: string
  user: UserInfo
}

export interface LoginRequest {
  email: string
  password: string
}

export interface RegisterRequest {
  email: string
  password: string
  fullName: string
}

export const authApi = {
  login: (request: LoginRequest) =>
    http.post<AuthResponse>('/api/auth/login', request).then((r) => r.data),

  register: (request: RegisterRequest) =>
    http.post<AuthResponse>('/api/auth/register', request).then((r) => r.data),

  refresh: () => http.post<AuthResponse>('/api/auth/refresh').then((r) => r.data),

  logout: () => http.post<void>('/api/auth/logout').then((r) => r.data),

  me: () => http.get<UserInfo>('/api/auth/me').then((r) => r.data),
}
