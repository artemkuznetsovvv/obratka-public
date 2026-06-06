import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { authApi, type LoginRequest, type RegisterRequest, type UserInfo } from '@/api/auth'
import { tokenStore } from './token-store'

interface AuthState {
  user: UserInfo | null
  isLoading: boolean
  login: (request: LoginRequest) => Promise<UserInfo>
  register: (request: RegisterRequest) => Promise<UserInfo>
  logout: () => Promise<void>
  // Обновить пользователя в контексте (после смены профиля), чтобы шапка/сайдбар освежились.
  updateUser: (user: UserInfo) => void
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserInfo | null>(null)
  const [isLoading, setIsLoading] = useState(true)

  const clearAuth = useCallback(() => {
    tokenStore.set(null)
    setUser(null)
  }, [])

  // Try to restore session via refresh cookie on mount
  useEffect(() => {
    let cancelled = false
    authApi
      .refresh()
      .then((res) => {
        if (cancelled) return
        tokenStore.set(res.accessToken)
        setUser(res.user)
      })
      .catch(() => {
        if (cancelled) return
        clearAuth()
      })
      .finally(() => {
        if (!cancelled) setIsLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [clearAuth])

  // Register logout handler so axios interceptor can trigger us
  useEffect(() => {
    tokenStore.registerLogoutHandler(() => {
      setUser(null)
    })
  }, [])

  const login = useCallback(async (request: LoginRequest) => {
    const res = await authApi.login(request)
    tokenStore.set(res.accessToken)
    setUser(res.user)
    return res.user
  }, [])

  const register = useCallback(async (request: RegisterRequest) => {
    const res = await authApi.register(request)
    tokenStore.set(res.accessToken)
    setUser(res.user)
    return res.user
  }, [])

  const logout = useCallback(async () => {
    try {
      await authApi.logout()
    } catch {
      // best effort; still clear local state
    }
    clearAuth()
  }, [clearAuth])

  const updateUser = useCallback((next: UserInfo) => setUser(next), [])

  const value = useMemo<AuthState>(
    () => ({ user, isLoading, login, register, logout, updateUser }),
    [user, isLoading, login, register, logout, updateUser],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
