import { Navigate, Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from '@/auth/ProtectedRoute'
import { useAuth } from '@/auth/AuthContext'
import { AuthLayout } from '@/layouts/AuthLayout'
import AuthPage from '@/pages/auth/AuthPage'
import UsersPage from '@/pages/admin/UsersPage'
import RequestsPage from '@/pages/admin/RequestsPage'
import ProxiesPage from '@/pages/admin/ProxiesPage'
import ParserTasksPage from '@/pages/admin/ParserTasksPage'
import AnalysesPage from '@/pages/admin/AnalysesPage'
import AnalysisDetailPage from '@/pages/admin/AnalysisDetailPage'
import CompaniesPage from '@/pages/admin/CompaniesPage'
import NewAnalysisPage from '@/pages/analyses/NewAnalysisPage'
import BranchSearchPage from '@/pages/analyses/BranchSearchPage'
import AnalysisSummaryPage from '@/pages/analyses/AnalysisSummaryPage'
import HistoryListPage from '@/pages/history/HistoryListPage'
import HistoryDetailPage from '@/pages/history/HistoryDetailPage'
import DashboardPage from '@/pages/dashboards/DashboardPage'
import MonitoringListPage from '@/pages/monitoring/MonitoringListPage'
import MonitoringDetailPage from '@/pages/monitoring/MonitoringDetailPage'

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
            <MonitoringListPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/monitoring/:id"
        element={
          <ProtectedRoute>
            <MonitoringDetailPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/history"
        element={
          <ProtectedRoute>
            <HistoryListPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/history/:jobId"
        element={
          <ProtectedRoute>
            <HistoryDetailPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/history/:jobId/dashboard"
        element={
          <ProtectedRoute>
            <DashboardPage />
          </ProtectedRoute>
        }
      />

      <Route
        path="/analyses/new"
        element={
          <ProtectedRoute>
            <NewAnalysisPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/analyses/new/:companyId/branches"
        element={
          <ProtectedRoute>
            <BranchSearchPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/analyses/new/:companyId/summary"
        element={
          <ProtectedRoute>
            <AnalysisSummaryPage />
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
        path="/admin/requests"
        element={
          <ProtectedRoute requiredRole="Admin">
            <RequestsPage />
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
      <Route
        path="/admin/analyses/:jobId"
        element={
          <ProtectedRoute requiredRole="Admin">
            <AnalysisDetailPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/admin/companies"
        element={
          <ProtectedRoute requiredRole="Admin">
            <CompaniesPage />
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
