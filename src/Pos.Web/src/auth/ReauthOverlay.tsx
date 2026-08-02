import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { isProblemError } from '@/api/problem'
import { useAuth } from './authContext'

/**
 * Re-authentication, rendered **over** the current screen.
 *
 * This is the whole point of the `expired` session status. The obvious
 * implementation — redirect to `/login` when the token dies — unmounts the
 * route tree, and Phase 5's cart is React state inside it. A cashier who has
 * scanned fourteen items and watches them vanish because a fifteen-minute token
 * expired will not use the product twice.
 *
 * So: no navigation, no unmount. A modal on top, and the screen underneath is
 * exactly as it was when the session ended.
 *
 * Built in Phase 4, where there is nothing to lose yet, precisely so Phase 5
 * inherits it working rather than discovering the requirement with a cart on
 * screen.
 */
export function ReauthOverlay() {
  const { user, tenant, login } = useAuth()

  // Prefilled from the session that just ended: it is the same person coming
  // back, and retyping a tenant slug mid-transaction is friction for nothing.
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const tenantSlug = tenant?.slug ?? ''

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      await login({ tenantSlug, email, password })
      setPassword('')
    } catch (caught) {
      setError(
        isProblemError(caught)
          ? (caught.problem.detail ?? caught.problem.title ?? 'Sign-in failed.')
          : 'Could not reach the server.',
      )
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="reauth-title"
      className="fixed inset-0 z-40 flex items-center justify-center bg-background/80 p-4 backdrop-blur-sm"
    >
      <form
        onSubmit={(event) => {
          void onSubmit(event)
        }}
        className="w-full max-w-sm rounded-xl border border-border bg-card p-6 shadow-lg"
      >
        <h2 id="reauth-title" className="text-lg font-semibold text-card-foreground">
          Session expired
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Sign in again to carry on. Nothing on this screen has been lost.
        </p>

        <div className="mt-5 flex flex-col gap-4">
          <Field label="Email">
            {(fieldProps) => (
              <Input
                {...fieldProps}
                type="email"
                autoComplete="username"
                value={email}
                placeholder={user?.displayName ?? ''}
                onChange={(event) => {
                  setEmail(event.target.value)
                }}
                required
              />
            )}
          </Field>

          <Field label="Password" error={error ?? undefined}>
            {(fieldProps) => (
              <Input
                {...fieldProps}
                type="password"
                autoComplete="current-password"
                // Focused on mount: the till is mid-transaction and the only
                // thing standing between the cashier and carrying on.
                autoFocus
                value={password}
                onChange={(event) => {
                  setPassword(event.target.value)
                }}
                required
              />
            )}
          </Field>
        </div>

        <Button type="submit" size="lg" className="mt-6 w-full" disabled={submitting}>
          {submitting ? 'Signing in…' : 'Continue'}
        </Button>
      </form>
    </div>
  )
}
