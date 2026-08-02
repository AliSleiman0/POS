import { useId, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

interface FieldProps {
  label: string
  /** Rendered under the control, in the destructive colour. */
  error?: string | undefined
  /** Rendered under the control when there is no error. */
  hint?: string | undefined
  required?: boolean
  className?: string
  /**
   * Receives the ids to wire up. Passing them through rather than cloning the
   * child keeps this a plain function and works with any control.
   */
  children: (props: {
    id: string
    'aria-invalid': boolean
    'aria-describedby': string | undefined
  }) => ReactNode
}

/**
 * Label + control + one message, wired for screen readers.
 *
 * The error is announced (`role="alert"`) because a validation failure a
 * sighted user sees appear next to the input is otherwise silent.
 */
export function Field({ label, error, hint, required = false, className, children }: FieldProps) {
  const id = useId()
  const messageId = `${id}-message`
  const hasMessage = error !== undefined || hint !== undefined

  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <label htmlFor={id} className="text-sm font-medium text-foreground">
        {label}
        {required ? (
          <span className="ml-0.5 text-destructive" aria-hidden="true">
            *
          </span>
        ) : null}
      </label>

      {children({
        id,
        'aria-invalid': error !== undefined,
        'aria-describedby': hasMessage ? messageId : undefined,
      })}

      {error !== undefined ? (
        <p id={messageId} role="alert" className="text-xs text-destructive">
          {error}
        </p>
      ) : hint !== undefined ? (
        <p id={messageId} className="text-xs text-muted-foreground">
          {hint}
        </p>
      ) : null}
    </div>
  )
}
