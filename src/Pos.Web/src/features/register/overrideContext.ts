/**
 * The manager-authorisation context and its hook.
 *
 * Separate from `OverrideProvider.tsx` for the same reason `cartContext.ts` is
 * separate from `CartProvider.tsx`: a module exporting both a component and a
 * hook breaks Fast Refresh.
 */

import { createContext, use } from 'react'
import type { Policy } from '@/auth/policies'

/** A manager's authorisation, as the register holds it. */
export interface OverrideAuthorization {
  /**
   * The opaque grant, presented as `X-Override-Authorization`.
   *
   * **Never persisted.** It is a credential with a five-minute life that the
   * server spends on the first sale it authorises; writing it to
   * `sessionStorage` alongside the cart would put a live authorisation on disk
   * for anyone with the tablet.
   */
  grant: string
  policies: readonly Policy[]
  authorizedByName: string
}

export interface OverrideContextValue {
  /** The held authorisation, if a manager has given one for this cart. */
  authorization: OverrideAuthorization | null

  /**
   * Ensures an authorisation covering `policies`, prompting for a manager's PIN
   * if the held one does not.
   *
   * Resolves to the authorisation, or `null` if the manager was not able or
   * willing to give one — in which case the caller must change nothing.
   */
  authorize: (policies: readonly Policy[]) => Promise<OverrideAuthorization | null>

  /** Forgets the authorisation. Called when the cart is cleared or sold. */
  clear: () => void
}

export const OverrideContext = createContext<OverrideContextValue | null>(null)

export function useOverride(): OverrideContextValue {
  const context = use(OverrideContext)

  if (context === null) {
    throw new Error('useOverride must be used inside an <OverrideProvider>')
  }

  return context
}
