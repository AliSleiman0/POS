/**
 * The toast context and its hook.
 *
 * Separate from `toast.tsx` for the same reason as `authContext.ts`: a module
 * that exports both a component and something else breaks Fast Refresh.
 */

import { createContext, use } from 'react'

export type ToastTone = 'info' | 'success' | 'error'

export interface ToastContextValue {
  show: (message: string, options?: { tone?: ToastTone; detail?: string }) => void
  /** Renders a thrown error, unwrapping `problem+json` when that is what it is. */
  showError: (error: unknown, fallback?: string) => void
  dismiss: (id: number) => void
}

export const ToastContext = createContext<ToastContextValue | null>(null)

export function useToast(): ToastContextValue {
  const context = use(ToastContext)

  if (context === null) {
    throw new Error('useToast must be used inside a <ToastProvider>')
  }

  return context
}
