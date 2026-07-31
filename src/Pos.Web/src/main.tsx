import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import './index.css'
import App from './App.tsx'

// Server state lives in TanStack Query; local UI state in React. No global
// store for data the server owns — see CLAUDE.md.
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // A POS sits open on one screen for a whole shift. Refetching on every
      // window focus hammers the API from an idle till for no benefit.
      refetchOnWindowFocus: false,
      staleTime: 30_000,
    },
  },
})

// The template used `document.getElementById('root')!`. CLAUDE.md forbids a
// bare `!` without justification, and this one is avoidable: a missing root
// should be a named error, not a null-deref stack trace.
const container = document.getElementById('root')
if (!container) {
  throw new Error("Root element '#root' was not found in index.html")
}

createRoot(container).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>
  </StrictMode>,
)
