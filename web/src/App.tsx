import { QueryClientProvider, type QueryClient } from '@tanstack/react-query'
import { Navigate, Route, Routes } from 'react-router'
import { AuthProvider } from './auth/AuthContext'
import { RequireAuth, RequireRole } from './components/Layout'
import { paths } from './lib/routes'
import { AgentDisputesPage } from './pages/AgentDisputesPage'
import { DisputeDetailPage } from './pages/DisputeDetailPage'
import { DisputesPage } from './pages/DisputesPage'
import { LoginPage } from './pages/LoginPage'
import { NotFoundPage } from './pages/NotFoundPage'
import { RaiseDisputePage } from './pages/RaiseDisputePage'
import { TransactionsPage } from './pages/TransactionsPage'

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<RequireAuth />}>
        <Route element={<RequireRole role="customer" />}>
          <Route index element={<Navigate to={paths.transactions} replace />} />
          <Route path={paths.transactions} element={<TransactionsPage />} />
          <Route path={paths.raiseDispute} element={<RaiseDisputePage />} />
          <Route path={paths.disputes} element={<DisputesPage />} />
          <Route path={paths.disputeDetail} element={<DisputeDetailPage />} />
        </Route>
        <Route element={<RequireRole role="agent" />}>
          <Route path={paths.agentDisputes} element={<AgentDisputesPage />} />
        </Route>
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  )
}

export function App({ queryClient }: { queryClient: QueryClient }) {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <AppRoutes />
      </AuthProvider>
    </QueryClientProvider>
  )
}
