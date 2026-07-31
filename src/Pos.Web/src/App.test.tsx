import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App'

function renderApp() {
  // retry: false so a failed probe surfaces immediately instead of the test
  // waiting out TanStack Query's backoff.
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('App shell', () => {
  it('renders the shadcn Button', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('Healthy', { status: 200 }))
    renderApp()
    expect(await screen.findByRole('button', { name: 'Re-check' })).toBeInTheDocument()
  })

  it('reports the API as healthy when /health/ready returns 200', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('Healthy', { status: 200 }))
    renderApp()
    expect(await screen.findByText('Healthy')).toBeInTheDocument()
  })

  it('surfaces a failed probe rather than silently showing nothing', async () => {
    // A dead API must be visible. Silence at a till reads as "it worked".
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('Unhealthy', { status: 503 }))
    renderApp()
    expect(await screen.findByText('Unhealthy')).toBeInTheDocument()
  })
})
