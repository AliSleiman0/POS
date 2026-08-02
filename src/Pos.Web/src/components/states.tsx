/**
 * Loading, empty and error states.
 *
 * Every list gets all three (Phase 4.3's exit criteria). The empty one matters
 * most: an empty catalog is the first thing a new tenant sees, and a blank
 * panel reads as a broken screen rather than as "add your first product".
 */

import type { ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { isProblemError } from '@/api/problem'
import { cn } from '@/lib/utils'

export function LoadingState({ label = 'Loading…' }: { label?: string }) {
  return (
    <div
      role="status"
      className="flex items-center justify-center gap-3 py-12 text-sm text-muted-foreground"
    >
      <span
        aria-hidden="true"
        className="size-4 animate-spin rounded-full border-2 border-muted-foreground/30 border-t-muted-foreground"
      />
      {label}
    </div>
  )
}

export function EmptyState({
  title,
  description,
  action,
  className,
}: {
  title: string
  description?: string
  action?: ReactNode
  className?: string
}) {
  return (
    <div
      className={cn(
        'flex flex-col items-center gap-2 rounded-lg border border-dashed border-border py-12 text-center',
        className,
      )}
    >
      <p className="text-sm font-medium text-foreground">{title}</p>
      {description !== undefined ? (
        <p className="max-w-sm text-sm text-muted-foreground">{description}</p>
      ) : null}
      {action !== undefined ? <div className="mt-2">{action}</div> : null}
    </div>
  )
}

/**
 * A failed load, with what went wrong and a way to try again.
 *
 * Reads `problem+json` when that is what it got. Never shows a stack or a raw
 * status code alone — "500" tells a shopkeeper nothing they can act on.
 */
export function ErrorState({
  error,
  onRetry,
  title = 'That did not load.',
}: {
  error: unknown
  onRetry?: () => void
  title?: string
}) {
  const detail = isProblemError(error)
    ? (error.problem.detail ?? error.problem.title)
    : error instanceof Error
      ? error.message
      : undefined

  return (
    <div
      role="alert"
      className="flex flex-col items-center gap-2 rounded-lg border border-destructive/30 bg-destructive/5 py-12 text-center"
    >
      <p className="text-sm font-medium text-destructive">{title}</p>
      {detail !== undefined ? (
        <p className="max-w-md text-sm text-muted-foreground">{detail}</p>
      ) : null}
      {onRetry !== undefined ? (
        <Button variant="outline" className="mt-2" onClick={onRetry}>
          Try again
        </Button>
      ) : null}
    </div>
  )
}
