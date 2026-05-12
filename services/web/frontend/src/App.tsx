import { Navigate, Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from '@/auth/ProtectedRoute'
import { useAuth } from '@/auth/AuthContext'
import { AuthLayout } from '@/layouts/AuthLayout'
import AuthPage from '@/pages/auth/AuthPage'
import UsersPage from '@/pages/admin/UsersPage'
import ProxiesPage from '@/pages/admin/ProxiesPage'
import ParserTasksPage from '@/pages/admin/ParserTasksPage'
import AnalysesPage from '@/pages/admin/AnalysesPage'
import ComingSoonPage from '@/pages/ComingSoonPage'

export default function App() {
  return (
    <Routes>
      <Route
        path="/login"
        element={
          <AuthLayout>
            <AuthPage />
          </AuthLayout>
        }
      />

      <Route path="/" element={<RoleBasedHome />} />

      <Route
        path="/monitoring"
        element={
          <ProtectedRoute>
            <ComingSoonPage title="Live-мониторинг" breadcrumbs={[{ label: 'Live-мониторинг' }]} />
          </ProtectedRoute>
        }
      />
      <Route
        path="/history"
        element={
          <ProtectedRoute>
            <ComingSoonPage title="История анализов" breadcrumbs={[{ label: 'История анализов' }]} />
          </ProtectedRoute>
        }
      />

      <Route
        path="/admin/users"
        element={
          <ProtectedRoute requiredRole="Admin">
            <UsersPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/admin/proxies"
        element={
          <ProtectedRoute requiredRole="Admin">
            <ProxiesPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/admin/parser-tasks"
        element={
          <ProtectedRoute requiredRole="Admin">
            <ParserTasksPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/admin/analyses"
        element={
          <ProtectedRoute requiredRole="Admin">
            <AnalysesPage />
          </ProtectedRoute>
        }
      />

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

function RoleBasedHome() {
  const { user, isLoading } = useAuth()
  if (isLoading) {
    return (
      <div className="flex h-screen items-center justify-center text-text-secondary text-sm">
        Загрузка…
      </div>
    )
  }
  if (!user) return <Navigate to="/login" replace />
  if (user.roles.includes('Admin')) return <Navigate to="/admin/users" replace />
  return <Navigate to="/monitoring" replace />
}
