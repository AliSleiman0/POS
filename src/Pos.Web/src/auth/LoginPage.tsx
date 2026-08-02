import { useState, type FormEvent } from 'react'
import { Link, Navigate, useLocation, useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { isProblemError } from '@/api/problem'
import { useAuth } from './authContext'
import { isDeviceEnrolled } from './deviceToken'

interface LocationState {
  from?: string
}

/**
 * Credential login: tenant slug + email + password.
 *
 * The slug is not decoration. `POST /auth/login` resolves the tenant row first
 * and only then looks for a user inside it, which is what keeps
 * `application_user`, `refresh_token` and `register` under the query filter and
 * under RLS — the three tables an attacker would most like to read across
 * tenants. See ARCHITECTURE.md.
 */
export function LoginPage() {
  const { status, login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()

  const [tenantSlug, setTenantSlug] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  if (status === 'authenticated') {
    return <Navigate to="/" replace />
  }

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      await login({ tenantSlug, email, password })
      const from = (location.state as LocationState | null)?.from
      await navigate(from ?? '/', { replace: true })
    } catch (caught) {
      // One message for unknown slug, unknown email and wrong password — the
      // server answers all three identically and with comparable timing, and a
      // client that distinguished them would undo that.
      setError(
        isProblemError(caught)
          ? (caught.problem.detail ?? 'The tenant, email address or password is incorrect.')
          : 'Could not reach the server. Check the connection and try again.',
      )
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <main className="flex h-full items-center justify-center p-4">
      <form
        onSubmit={(event) => {
          void onSubmit(event)
        }}
        className="w-full max-w-sm rounded-xl border border-border bg-card p-8 shadow-sm"
      >
        <h1 className="text-2xl font-semibold text-card-foreground">Sign in</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Your shop&rsquo;s name, then your own credentials.
        </p>

        <div className="mt-6 flex flex-col gap-4">
          <Field label="Shop" required hint="The short name your shop was set up with.">
            {(fieldProps) => (
              <Input
                {...fieldProps}
                name="tenantSlug"
                autoComplete="organization"
                autoFocus
                value={tenantSlug}
                onChange={(event) => {
                  setTenantSlug(event.target.value)
                }}
                required
              />
            )}
          </Field>

          <Field label="Email" required>
            {(fieldProps) => (
              <Input
                {...fieldProps}
                name="email"
                type="email"
                autoComplete="username"
                value={email}
                onChange={(event) => {
                  setEmail(event.target.value)
                }}
                required
              />
            )}
          </Field>

          <Field label="Password" required error={error ?? undefined}>
            {(fieldProps) => (
              <Input
                {...fieldProps}
                name="password"
                type="password"
                autoComplete="current-password"
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
          {submitting ? 'Signing in…' : 'Sign in'}
        </Button>

        {isDeviceEnrolled() ? (
          <p className="mt-4 text-center text-sm text-muted-foreground">
            On the till?{' '}
            <Link to="/pin" className="text-primary underline underline-offset-4">
              Use your PIN
            </Link>
          </p>
        ) : null}
      </form>
    </main>
  )
}
