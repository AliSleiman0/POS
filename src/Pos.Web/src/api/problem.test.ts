import { describe, expect, it } from 'vitest'
import { ErrorType, isErrorType, ProblemError, toProblem } from './problem'

/**
 * `problem+json` handling.
 *
 * The rule under test is that clients branch on `type` and never on `detail`.
 * `DomainExceptionHandler` documents `detail` as human-facing prose that may be
 * reworded at any time, so a UI matching on the message is one copy edit away
 * from silently stopping handling the case it was written for.
 */
describe('ProblemError', () => {
  const duplicateSku = new ProblemError(409, {
    type: 'https://pos.example/errors/duplicate-sku',
    title: 'SKU already in use',
    status: 409,
    detail: "The SKU 'APPLE-01' is already used by another product.",
    traceId: '00-abc-def-01',
  })

  it('exposes the slug with the URI prefix stripped', () => {
    expect(duplicateSku.slug).toBe('duplicate-sku')
    expect(duplicateSku.is(ErrorType.duplicateSku)).toBe(true)
    expect(duplicateSku.is(ErrorType.duplicateBarcode)).toBe(false)
  })

  it('carries the detail as its message, so an unhandled throw still reads', () => {
    expect(duplicateSku.message).toContain('APPLE-01')
  })

  it('has no slug when the type is absent or unfamiliar', () => {
    expect(new ProblemError(500, {}).slug).toBeUndefined()
    // Something in front of the API — a gateway — answering in its own format.
    expect(new ProblemError(502, { type: 'about:blank' }).slug).toBeUndefined()
  })

  describe('fieldErrors', () => {
    it('reads a validation problem', () => {
      const validation = new ProblemError(400, {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: {
          sku: ['A SKU of 1 to 64 characters is required.'],
          unitPrice: ['A price of 0 or more with at most 4 decimal places is required.'],
        },
      })

      expect(validation.fieldError('sku')).toBe('A SKU of 1 to 64 characters is required.')
      expect(Object.keys(validation.fieldErrors)).toEqual(['sku', 'unitPrice'])
    })

    it('is empty rather than throwing on a problem that has no errors map', () => {
      // The backend equivalent of this trap is real and cost a debugging
      // session: `GetProperty("errors")` on a 409 throws KeyNotFoundException,
      // which reads in a log as a failure about something else entirely. A form
      // renders field errors unconditionally, so this has to be total.
      expect(duplicateSku.fieldErrors).toEqual({})
      expect(duplicateSku.fieldError('sku')).toBeUndefined()
    })
  })

  it('does not compare traceId, which differs on every response', () => {
    const again = new ProblemError(409, { ...duplicateSku.problem, traceId: '00-zzz-yyy-99' })

    // Same failure, different trace. Branching is on `type` alone.
    expect(again.slug).toBe(duplicateSku.slug)
  })
})

describe('isErrorType', () => {
  it('narrows only for a matching ProblemError', () => {
    const locked = new ProblemError(401, {
      type: 'https://pos.example/errors/account-locked',
      lockoutEndsAt: '2026-08-02T14:05:00Z',
    })

    expect(isErrorType(locked, ErrorType.accountLocked)).toBe(true)
    expect(isErrorType(locked, ErrorType.invalidCredentials)).toBe(false)
    // A network failure is an Error, not a ProblemError.
    expect(isErrorType(new Error('Failed to fetch'), ErrorType.accountLocked)).toBe(false)
    expect(isErrorType(undefined, ErrorType.accountLocked)).toBe(false)
  })

  it('reaches the extension a problem carries', () => {
    const locked = new ProblemError(401, {
      type: 'https://pos.example/errors/account-locked',
      lockoutEndsAt: '2026-08-02T14:05:00Z',
    })

    // Rendered as "try again at 14:05" rather than a flat refusal.
    expect(locked.problem.lockoutEndsAt).toBe('2026-08-02T14:05:00Z')
  })
})

describe('toProblem', () => {
  it('passes a real problem body through', () => {
    const body = { type: 'https://pos.example/errors/under-tender', status: 409 }
    expect(toProblem(409, body)).toBe(body)
  })

  it('synthesises a body for a failure that has none', () => {
    // `limit=abc` currently returns a bare 400 with no body — a known gap. It
    // still has to become a ProblemError rather than an unhandled `undefined`.
    const problem = toProblem(400, '', 'Bad Request')

    expect(problem.title).toBe('Bad Request')
    expect(problem.status).toBe(400)
  })

  it('keeps a plain-text body as the detail', () => {
    expect(toProblem(502, 'upstream connect error').detail).toBe('upstream connect error')
  })

  it('falls back to the status when there is nothing at all', () => {
    expect(toProblem(500, null).title).toBe('HTTP 500')
  })
})
