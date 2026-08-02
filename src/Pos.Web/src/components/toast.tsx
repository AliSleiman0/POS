/**
 * In-page notifications.
 *
 * CLAUDE.md invariant 10: **no `alert()`, `confirm()` or `prompt()`.** They
 * block the page, they block a queue behind them, and on a till that means a
 * customer standing at a frozen screen. Everything that would have been a
 * browser dialog is one of these or the in-page confirm in `ConfirmButton`.
 *
 * The companion rule: a failed API call must never be a silent no-op. Mutations
 * route their errors here, so "nothing happened" is never the whole feedback.
 */

import { useCallback, useMemo, useRef, useState, type ReactNode } from 'react'
import { cn } from '@/lib/utils'
import { isProblemError } from '@/api/problem'
import { ToastContext, type ToastContextValue, type ToastTone } from './toastContext'

interface Toast {
  id: number
  tone: ToastTone
  message: string
  /** Extra line, e.g. a problem's `detail` under its `title`. */
  detail?: string | undefined
}

/** How long a toast stays. Errors linger — a cashier may be mid-transaction. */
const DURATION_MS: Record<ToastTone, number> = {
  info: 4000,
  success: 4000,
  error: 10_000,
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const nextId = useRef(0)

  const dismiss = useCallback((id: number) => {
    setToasts((current) => current.filter((toast) => toast.id !== id))
  }, [])

  const show = useCallback<ToastContextValue['show']>(
    (message, options) => {
      const tone = options?.tone ?? 'info'
      const id = nextId.current++

      setToasts((current) => [...current, { id, tone, message, detail: options?.detail }])
      setTimeout(() => {
        dismiss(id)
      }, DURATION_MS[tone])
    },
    [dismiss],
  )

  const showError = useCallback<ToastContextValue['showError']>(
    (error, fallback = 'Something went wrong.') => {
      if (isProblemError(error)) {
        // `title` is the stable summary; `detail` is the prose. Both are shown,
        // because at a counter "SKU already in use" alone does not say which.
        show(error.problem.title ?? fallback, {
          tone: 'error',
          detail: error.problem.detail,
        })
        return
      }

      if (error instanceof Error) {
        // A genuine network failure — the API is unreachable, not refusing.
        show(fallback, { tone: 'error', detail: error.message })
        return
      }

      show(fallback, { tone: 'error' })
    },
    [show],
  )

  const value = useMemo<ToastContextValue>(
    () => ({ show, showError, dismiss }),
    [show, showError, dismiss],
  )

  return (
    <ToastContext value={value}>
      {children}
      <ToastViewport toasts={toasts} onDismiss={dismiss} />
    </ToastContext>
  )
}

function ToastViewport({
  toasts,
  onDismiss,
}: {
  toasts: readonly Toast[]
  onDismiss: (id: number) => void
}) {
  return (
    // `aria-live` so a toast is announced without stealing focus — focus theft
    // mid-scan would drop the next barcode.
    <div
      aria-live="polite"
      aria-atomic="false"
      className="pointer-events-none fixed inset-x-0 bottom-0 z-50 flex flex-col items-center gap-2 p-4 sm:items-end"
    >
      {toasts.map((toast) => (
        <div
          key={toast.id}
          data-slot="toast"
          data-tone={toast.tone}
          className={cn(
            'pointer-events-auto w-full max-w-sm rounded-lg border px-4 py-3 shadow-lg',
            toast.tone === 'error' && 'border-destructive/30 bg-destructive/10 text-destructive',
            toast.tone === 'success' && 'border-border bg-card text-card-foreground',
            toast.tone === 'info' && 'border-border bg-card text-card-foreground',
          )}
        >
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <p className="text-sm font-medium">{toast.message}</p>
              {toast.detail !== undefined ? (
                <p className="mt-0.5 text-xs opacity-80">{toast.detail}</p>
              ) : null}
            </div>
            <button
              type="button"
              onClick={() => {
                onDismiss(toast.id)
              }}
              className="shrink-0 text-xs underline underline-offset-2 opacity-70 hover:opacity-100"
            >
              Dismiss
            </button>
          </div>
        </div>
      ))}
    </div>
  )
}
