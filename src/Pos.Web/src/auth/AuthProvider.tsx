/**
 * The session: who is signed in, what they may do, and which shop they are in.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, deviceApi, unwrap } from '@/api/client'
import { isProblemError } from '@/api/problem'
import { refreshSession } from './refresh'
import {
  accessTokenExpiresAt,
  clearTokens,
  getRefreshToken,
  onTokensCleared,
  setTokens,
} from './tokenStore'
import { hasPolicy, type Policy } from './policies'
import {
  AuthContext,
  type AuthContextValue,
  type AuthResponse,
  type SessionStatus,
} from './authContext'

/** Refresh this long before expiry. Matches `tokenStore`'s own margin. */
const REFRESH_MARGIN_MS = 60_000

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()

  const [status, setStatus] = useState<SessionStatus>(() =>
    getRefreshToken() !== null ? 'loading' : 'anonymous',
  )

  // Read inside the `onTokensCleared` listener, which is registered once and
  // would otherwise close over the status it was registered with.
  const statusRef = useRef(status)
  statusRef.current = status

  // Everything except `anonymous`, so a retry from the `unreachable` state can
  // actually run — a disabled query cannot be refetched.
  const enabled = status !== 'anonymous'

  const me = useQuery({
    queryKey: ['auth', 'me'],
    queryFn: () => unwrap(api.GET('/api/v1/auth/me')),
    enabled,
    // The session is not something to be stale about, but it also does not
    // change on its own — refetching is driven by login/logout, not a timer.
    staleTime: Number.POSITIVE_INFINITY,
    // Retry is left to the shared policy in `queryClient.ts`, which already
    // declines to retry a 4xx and does retry a transport failure. A blanket
    // `retry: false` here would turn one dropped packet into a failed restore.
  })

  /**
   * What a failed restore means.
   *
   * **A dropped connection is not a rejected credential.** `refresh.ts` makes
   * exactly this distinction one layer down — a `fetch` that throws keeps the
   * token — and it has to hold here too, because the failure mode is a till
   * reloading on a flaky shop connection and landing on a login screen with a
   * customer waiting. Only an answer that actually refused the credential ends
   * the session; anything else keeps the tokens and offers a retry.
   *
   * The client middleware has already tried to refresh by the time we get here,
   * so a 401 at this point is final.
   */
  useEffect(() => {
    if (!me.isError || statusRef.current !== 'loading') {
      return
    }

    if (isProblemError(me.error) && me.error.status === 401) {
      clearTokens()
      return
    }

    setStatus('unreachable')
  }, [me.isError, me.error])

  useEffect(() => {
    if (me.isSuccess && statusRef.current !== 'authenticated') {
      setStatus('authenticated')
    }
  }, [me.isSuccess])

  useEffect(
    () =>
      onTokensCleared(() => {
        // Only a *live* session becoming expired warrants the overlay. Failing
        // to restore one on page load is simply "not signed in".
        setStatus(statusRef.current === 'authenticated' ? 'expired' : 'anonymous')
      }),
    [],
  )

  /**
   * Silent refresh, scheduled from the token's own expiry.
   *
   * The client middleware also refreshes reactively, so this is not the only
   * defence — it exists so an idle till that has not made a request for twenty
   * minutes is still holding a usable token when the next customer arrives,
   * rather than paying a refresh round trip in front of them.
   */
  useEffect(() => {
    if (status !== 'authenticated') {
      return
    }

    const expiresAt = accessTokenExpiresAt()

    if (expiresAt === null) {
      return
    }

    const delay = Math.max(0, expiresAt - REFRESH_MARGIN_MS - Date.now())
    const timer = setTimeout(() => {
      void refreshSession()
    }, delay)

    return () => {
      clearTimeout(timer)
    }
    // `me.dataUpdatedAt` is not the trigger; a completed refresh replaces the
    // token, and the effect re-runs because `status` and the tick below change.
  }, [status, me.dataUpdatedAt])

  const adoptSession = useCallback(
    async (tokens: AuthResponse) => {
      // `expiresIn` is an int32, and .NET's OpenAPI describes every numeric as
      // `number | string` — so the generated type carries the union even though
      // the wire format is always a number. Resolved here rather than trusting
      // it, because `Date.now() + '900' * 1000` is NaN and the session would
      // then look permanently expired.
      setTokens({ ...tokens, expiresIn: Number(tokens.expiresIn) })

      // Drop everything the previous user could see before the next screen
      // paints. A PIN swap changes who is looking at the till, and a Cashier
      // must not inherit an Owner's cached cost prices.
      //
      // Everything *except* the session query. `clear()` would remove that one
      // too, and a query that no longer exists cannot be refetched — the
      // provider would sit holding the previous user's `me` result, so a swap
      // to a cashier would leave an owner's name and an owner's navigation on
      // screen with a cashier's token behind it.
      queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== 'auth' })

      setStatus('authenticated')

      // Refetch rather than invalidate: this must have *finished* before the
      // caller navigates, or the next screen renders against the old identity.
      await queryClient.refetchQueries({ queryKey: ['auth', 'me'] })
    },
    [queryClient],
  )

  const login = useCallback<AuthContextValue['login']>(
    async (credentials) => {
      const response = await unwrap(api.POST('/api/v1/auth/login', { body: credentials }))
      await adoptSession(response)
    },
    [adoptSession],
  )

  const pinLogin = useCallback<AuthContextValue['pinLogin']>(
    async (credentials) => {
      // The device-token client: `POST /auth/pin` is authenticated by the
      // DeviceToken *scheme*, so an unenrolled till is turned away before the
      // PIN is read. See `deviceApi` in api/client.ts.
      const response = await unwrap(deviceApi.POST('/api/v1/auth/pin', { body: credentials }))
      await adoptSession(response)
    },
    [adoptSession],
  )

  /**
   * Another go at a restore that could not reach the server.
   *
   * Back to `loading` first, so the retry shows as one and a second failure
   * runs through the same branch rather than being swallowed by the guard on
   * `statusRef` above.
   */
  const retrySession = useCallback(() => {
    setStatus('loading')
    void me.refetch()
  }, [me])

  const logout = useCallback(async () => {
    const refreshToken = getRefreshToken()

    if (refreshToken !== null) {
      try {
        // Revokes the whole refresh-token family server-side. Best-effort: a
        // logout that reported failure is a logout users retry, and the local
        // session is dropped either way.
        await unwrap(api.POST('/api/v1/auth/logout', { body: { refreshToken } }))
      } catch {
        // Intentionally swallowed — see above.
      }
    }

    clearTokens()
    setStatus('anonymous')
    queryClient.clear()
  }, [queryClient])

  // `?? []` allocates a fresh array on every render, so this must be memoised
  // or the context value below is new every time — which re-renders every
  // screen reading it, including the register, on every unrelated state change.
  const policies = useMemo(() => me.data?.user.policies ?? [], [me.data])

  const value = useMemo<AuthContextValue>(
    () => ({
      status,
      user: me.data?.user ?? null,
      tenant: me.data?.tenant ?? null,
      policies,
      can: (policy: Policy) => hasPolicy(policies, policy),
      login,
      pinLogin,
      logout,
      retrySession,
    }),
    [status, me.data, policies, login, pinLogin, logout, retrySession],
  )

  return <AuthContext value={value}>{children}</AuthContext>
}
