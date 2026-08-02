import { Component, type ErrorInfo, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'

interface Props {
  children: ReactNode
  /** Shown instead of the default panel. */
  fallback?: (error: Error, reset: () => void) => ReactNode
}

interface State {
  error: Error | null
}

/**
 * Catches a render-time crash so a till shows a message rather than a white
 * screen.
 *
 * Deliberately a class: React has no hook equivalent of `componentDidCatch`.
 *
 * This is the *last* line, not the mechanism. Failed API calls are handled
 * where they happen — `ErrorState` for a failed load, a toast for a failed
 * mutation. Anything reaching here is a bug in the UI itself.
 */
export class ErrorBoundary extends Component<Props, State> {
  override state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    // Console only. Phase 8.4 adds real error tracking; until then this is what
    // a developer with the tab open can see.
    console.error('Unhandled UI error', error, info.componentStack)
  }

  private readonly reset = (): void => {
    this.setState({ error: null })
  }

  override render(): ReactNode {
    const { error } = this.state

    if (error === null) {
      return this.props.children
    }

    if (this.props.fallback !== undefined) {
      return this.props.fallback(error, this.reset)
    }

    return (
      <div
        role="alert"
        className="flex h-full flex-col items-center justify-center gap-3 p-8 text-center"
      >
        <h1 className="text-lg font-semibold text-foreground">Something broke on this screen.</h1>
        <p className="max-w-md text-sm text-muted-foreground">
          Nothing was lost on the server — no sale, stock movement or shift is affected by this. Try
          again, and if it keeps happening the message below is what to report.
        </p>
        <p className="max-w-md rounded-lg border border-border bg-muted px-3 py-2 font-mono text-xs text-muted-foreground">
          {error.message}
        </p>
        <div className="mt-2 flex gap-2">
          <Button variant="outline" onClick={this.reset}>
            Try again
          </Button>
          <Button
            onClick={() => {
              window.location.assign('/')
            }}
          >
            Back to the start
          </Button>
        </div>
      </div>
    )
  }
}
