import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider } from 'react-router'
import './index.css'
import { createQueryClient } from './app/queryClient'
import { ErrorBoundary } from './app/ErrorBoundary'
import { router } from './app/router'
import { AuthProvider } from './auth/AuthProvider'
import { ToastProvider } from './components/toast'

const queryClient = createQueryClient()

// The template used `document.getElementById('root')!`. CLAUDE.md forbids a
// bare `!` without justification, and this one is avoidable: a missing root
// should be a named error, not a null-deref stack trace.
const container = document.getElementById('root')
if (!container) {
  throw new Error("Root element '#root' was not found in index.html")
}

// Provider order is load-bearing. AuthProvider uses TanStack Query for
// `/auth/me`, so it is inside QueryClientProvider; the router's screens use
// both plus toasts, so it is innermost. The outermost ErrorBoundary catches a
// crash in the providers themselves, which the in-layout one cannot.
createRoot(container).render(
  <StrictMode>
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <AuthProvider>
            <RouterProvider router={router} />
          </AuthProvider>
        </ToastProvider>
      </QueryClientProvider>
    </ErrorBoundary>
  </StrictMode>,
)
