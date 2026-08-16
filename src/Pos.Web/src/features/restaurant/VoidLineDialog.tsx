import { useState } from 'react'
import { ErrorType, isErrorType } from '@/api/problem'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useToast } from '@/components/toastContext'
import { ManagerAuthorizationDialog } from '@/features/register/ManagerAuthorizationDialog'
import { useVoidLine, type OrderLine } from './queries'

/**
 * Taking a line off, with the distinction the status exists for.
 *
 * **A pending line costs the shop nothing** — nobody cooked it, the guest
 * changed their mind — so it comes off with one tap, no reason and no audit
 * entry. Filing a record for every one of those is how a log stops being read.
 *
 * **A fired line is a plate the shop has lost.** It needs `CanVoidFiredLine`, a
 * reason, and it is audited. On a shared handheld the person holding the device
 * is usually a waiter, so a manager authorises it here with a PIN rather than
 * signing in — see `ManagerAuthorizationDialog`, and the argument for putting
 * the policy on the grant allow-list in `docs/ARCHITECTURE.md`.
 *
 * **In-page, never `confirm()`** (invariant 10).
 */
export function VoidLineDialog({
  orderId,
  line,
  onClose,
}: {
  orderId: string
  line: OrderLine
  onClose: () => void
}) {
  const toast = useToast()
  const voidLine = useVoidLine(orderId)

  const [reason, setReason] = useState('')
  const [authorizing, setAuthorizing] = useState(false)

  const fired = line.status === 'Fired'

  const submit = async (grant: string | null) => {
    try {
      await voidLine.mutateAsync({
        lineId: line.id,
        reason: reason.trim() === '' ? null : reason.trim(),
        grant,
      })

      toast.show('Taken off.', { tone: 'success' })
      onClose()
    } catch (caught) {
      // The server's cue that a PIN can fix this, told apart from every other
      // refusal by a stable slug rather than by the bare status.
      if (isErrorType(caught, ErrorType.overrideRequired)) {
        setAuthorizing(true)
        return
      }

      toast.showError(caught, 'Could not take that off.')
    }
  }

  if (authorizing) {
    return (
      <ManagerAuthorizationDialog
        policies={['CanVoidFiredLine']}
        onSettle={(authorization) => {
          setAuthorizing(false)

          if (authorization !== null) {
            void submit(authorization.grant)
          }
        }}
      />
    )
  }

  return (
    <section
      aria-label={`Remove ${line.description}`}
      data-testid="void-line"
      className="mt-1 flex flex-col gap-2 rounded-lg border border-destructive/30 bg-destructive/5 p-2"
    >
      <p className="text-xs text-foreground">
        {fired
          ? 'This is already with the kitchen. It needs a reason, and a supervisor has to allow it.'
          : 'Nobody has cooked this yet.'}
      </p>

      {fired ? (
        <Input
          value={reason}
          onChange={(event) => setReason(event.target.value)}
          placeholder="Why is it coming off?"
          aria-label="Reason"
        />
      ) : null}

      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" size="sm" onClick={onClose}>
          Keep it
        </Button>
        <Button
          type="button"
          variant="destructive"
          size="sm"
          disabled={voidLine.isPending || (fired && reason.trim() === '')}
          data-testid="confirm-void"
          onClick={() => void submit(null)}
        >
          Remove
        </Button>
      </div>
    </section>
  )
}
