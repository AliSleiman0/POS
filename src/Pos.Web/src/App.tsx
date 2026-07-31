import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'

interface HealthState {
  status: 'checking' | 'up' | 'down'
  detail: string
}

async function fetchApiHealth(): Promise<string> {
  const response = await fetch('/health/ready')
  const body = (await response.text()).trim()
  if (!response.ok) {
    throw new Error(body || `HTTP ${response.status}`)
  }
  return body
}

/**
 * Scaffold shell for milestone 0.5. The real application shell — routing,
 * layout, error boundary and auth guards — arrives in Phase 4.
 *
 * The API probe is here deliberately: it proves the Vite dev proxy reaches the
 * backend, which is the one thing about this scaffold that can silently break.
 */
export default function App() {
  const { data, error, isPending, refetch } = useQuery({
    queryKey: ['health'],
    queryFn: fetchApiHealth,
    retry: false,
  })

  const health: HealthState = isPending
    ? { status: 'checking', detail: 'contacting API…' }
    : error
      ? { status: 'down', detail: error instanceof Error ? error.message : 'unreachable' }
      : { status: 'up', detail: data ?? 'Healthy' }

  const badge =
    health.status === 'up'
      ? 'bg-emerald-100 text-emerald-800'
      : health.status === 'down'
        ? 'bg-red-100 text-red-800'
        : 'bg-slate-100 text-slate-600'

  return (
    <main className="flex h-full flex-col items-center justify-center gap-6 bg-slate-50 p-8">
      <div className="w-full max-w-md rounded-xl border border-slate-200 bg-white p-8 shadow-sm">
        <h1 className="text-2xl font-semibold text-slate-900">POS</h1>
        <p className="mt-2 text-sm text-slate-600">
          Scaffold — milestone 0.5. The register screen arrives in Phase 5.
        </p>

        <div className="mt-6 flex items-center justify-between border-t border-slate-200 pt-4">
          <span className="text-sm font-medium text-slate-700">API</span>
          <span className={`rounded-full px-3 py-1 text-xs font-medium ${badge}`}>
            {health.detail}
          </span>
        </div>

        <Button
          variant="outline"
          className="mt-4 w-full"
          onClick={() => void refetch()}
          disabled={isPending}
        >
          Re-check
        </Button>
      </div>
    </main>
  )
}
