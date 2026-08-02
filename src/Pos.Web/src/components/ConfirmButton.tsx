import { useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'

interface ConfirmButtonProps {
  children: ReactNode
  /** Replaces the label once armed. */
  confirmLabel?: string
  onConfirm: () => void
  disabled?: boolean
  className?: string
}

/**
 * A destructive action that asks first, in the page.
 *
 * The alternative is `window.confirm`, which CLAUDE.md invariant 10 forbids
 * outright: it blocks the event loop, so a scanner firing keystrokes behind it
 * is queued and then replayed into whatever has focus afterwards.
 *
 * Two clicks with a visible state change between them. Disarms on blur, so an
 * armed button left alone does not fire on a stray click much later.
 */
export function ConfirmButton({
  children,
  confirmLabel = 'Confirm',
  onConfirm,
  disabled = false,
  className,
}: ConfirmButtonProps) {
  const [armed, setArmed] = useState(false)

  return (
    <Button
      type="button"
      variant="destructive"
      disabled={disabled}
      className={className}
      onBlur={() => {
        setArmed(false)
      }}
      onClick={() => {
        if (armed) {
          setArmed(false)
          onConfirm()
        } else {
          setArmed(true)
        }
      }}
    >
      {armed ? confirmLabel : children}
    </Button>
  )
}
