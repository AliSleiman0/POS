/**
 * RFC 9457 `application/problem+json`, as this API emits it.
 *
 * Every failure from `/api/v1` arrives in this shape — the backend routes them
 * all through one handler (`src/Pos.Api/Errors/DomainExceptionHandler.cs`) so
 * the contract cannot drift per route.
 *
 * **Branch on `type`, never on `detail`.** `type` is a stable slug; `detail` is
 * human-facing prose and the backend documents it as reworderable at any time.
 * A client that matched on the message would break on a copy edit.
 */

/** The base every domain error's `type` is prefixed with. */
const ERROR_TYPE_BASE = 'https://pos.example/errors/'

/**
 * The slugs the server can send, mirroring `PosDomainException.ErrorType` plus
 * the two `AuthEndpoints` raises directly.
 *
 * Listed exhaustively so a `switch` over them is checked, and so adding a
 * backend error without teaching the UI about it is a visible gap rather than a
 * silent fall-through to "something went wrong".
 */
export const ErrorType = {
  categoryCycle: 'category-cycle',
  concurrentStockUpdate: 'concurrent-stock-update',
  crossTenantWrite: 'cross-tenant-write',
  defaultTaxClassConflict: 'default-tax-class-conflict',
  duplicateBarcode: 'duplicate-barcode',
  duplicateSku: 'duplicate-sku',
  idempotencyKeyReused: 'idempotency-key-reused',
  invalidDiscount: 'invalid-discount',
  refundExceedsOriginal: 'refund-exceeds-original',
  saleAlreadyRefunded: 'sale-already-refunded',
  saleAlreadyVoided: 'sale-already-voided',
  shiftAlreadyOpen: 'shift-already-open',
  shiftClosed: 'shift-closed',
  tenantNotResolved: 'tenant-not-resolved',
  underTender: 'under-tender',

  // Not domain exceptions — TypedResults.Problem in AuthEndpoints and SaleEndpoints.
  invalidCredentials: 'invalid-credentials',
  accountLocked: 'account-locked',

  /** The cart carries a discount or an override the caller may not apply. A manager can. */
  overrideRequired: 'override-required',

  /** The PIN was right; that member of staff still cannot authorise it. */
  overrideNotPermitted: 'override-not-permitted',

  /**
   * A queued sale's `occurredAt` is outside what the server will date a row by —
   * a till whose clock is wrong, or a sale that has sat in the outbox too long.
   *
   * **Permanent.** Retrying sends the same body under the same key and gets the
   * same answer, so the outbox must move it to the review queue on the first
   * refusal rather than backing off against it for ever.
   */
  offlineSaleTimestampInvalid: 'offline-sale-timestamp-invalid',

  // ── Phase 10, the restaurant ────────────────────────────────────────────
  /** Every order, floor, menu and kitchen route answers this at a retail shop. */
  restaurantModeRequired: 'restaurant-mode-required',

  /** Two staff seated one table in the same second and this one lost the index. */
  tableAlreadyOccupied: 'table-already-occupied',

  /** It was settled or abandoned. The caller's move is to look at what it became. */
  orderNotOpen: 'order-not-open',

  /** A shop cannot go back to being a counter with tables still open. */
  ordersStillOpen: 'orders-still-open',

  /**
   * Something on the round has no station to be cooked at.
   *
   * **The whole fire is refused, not the line**, so nothing is written and the
   * retry after a manager fixes the menu is the same request. `detail` names the
   * products, which is the entire value of the refusal.
   */
  productNotRouted: 'product-not-routed',
} as const

export type ErrorTypeSlug = (typeof ErrorType)[keyof typeof ErrorType]

/** The body itself. `errors` is present only on a validation problem. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  errors?: Record<string, string[]>
  /** Per-request, and different on every response — never compare it. */
  traceId?: string
  /** `account-locked` carries this. Others carry their own extensions. */
  lockoutEndsAt?: string
  /** `override-required` carries the policies a manager would have to authorise. */
  requiredPolicies?: string[]
}

/**
 * A failed API call.
 *
 * Thrown by the client middleware, so a TanStack Query `error` is always one of
 * these or a genuine network failure — never a silently-ignored non-2xx.
 */
export class ProblemError extends Error {
  readonly status: number
  readonly problem: ProblemDetails

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `Request failed with status ${status}`)
    this.name = 'ProblemError'
    this.status = status
    this.problem = problem
  }

  /** The slug with the URI prefix stripped, or `undefined` if there was no `type`. */
  get slug(): string | undefined {
    const type = this.problem.type
    if (type === undefined || !type.startsWith(ERROR_TYPE_BASE)) {
      return undefined
    }
    return type.slice(ERROR_TYPE_BASE.length)
  }

  is(slug: ErrorTypeSlug): boolean {
    return this.slug === slug
  }

  /**
   * Per-field validation messages, or an empty object when this is not a
   * validation problem.
   *
   * Deliberately total. The equivalent backend trap is worth restating:
   * `GetProperty("errors")` on a non-validation problem throws, and reads in a
   * test log as an unrelated failure. Returning `{}` means a form can render
   * field errors unconditionally and a 409 simply produces none — the caller
   * checks {@link is} for those.
   */
  get fieldErrors(): Record<string, string[]> {
    return this.problem.errors ?? {}
  }

  /** The first message for a field, for rendering under an input. */
  fieldError(field: string): string | undefined {
    return this.fieldErrors[field]?.[0]
  }
}

export function isProblemError(error: unknown): error is ProblemError {
  return error instanceof ProblemError
}

/** True when `error` is a `ProblemError` carrying exactly this slug. */
export function isErrorType(error: unknown, slug: ErrorTypeSlug): boolean {
  return isProblemError(error) && error.is(slug)
}

/**
 * Narrows an unknown response body to a {@link ProblemDetails}.
 *
 * Tolerant on purpose: a few failures never reach the domain handler and come
 * back as a bare status with no body at all — `limit=abc` is one, and an
 * infrastructure 502 in front of the API is another. Those still have to become
 * a `ProblemError` rather than an unhandled `undefined`, so the shape is
 * best-effort and a synthetic title fills in.
 */
export function toProblem(status: number, body: unknown, statusText?: string): ProblemDetails {
  if (typeof body === 'object' && body !== null) {
    return body as ProblemDetails
  }

  return {
    title: statusText !== undefined && statusText !== '' ? statusText : `HTTP ${status}`,
    status,
    ...(typeof body === 'string' && body !== '' ? { detail: body } : {}),
  }
}
