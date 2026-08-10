/**
 * Client validation that **mirrors** the server's, and does not replace it.
 *
 * `EmployeeEndpoints.ValidateCreate` is the authority, and Identity's password
 * rules sit behind it. Everything here saves a round trip on a mistake the user
 * can already see; anything it lets through comes back as a `problem+json` with
 * a per-field `errors` map and lands on the same field.
 *
 * **The password rules are deliberately not mirrored.** Identity owns them
 * (`IdentityServiceCollectionExtensions`), its messages name the specific rule
 * that was broken, and a second copy here would go stale the first time an
 * option changes — leaving the form confidently wrong about a password the
 * server would have accepted. Only emptiness is checked.
 */

import type { FieldErrors } from '@/features/catalog/validation'

/** `ApplicationUser.DisplayNameMaxLength`. */
export const DISPLAY_NAME_MAX_LENGTH = 100
/** `ApplicationUser.EmailMaxLength`. */
export const EMAIL_MAX_LENGTH = 256

/** `Pin.MinLength` and `Pin.MaxLength`. */
export const PIN_MIN_LENGTH = 4
export const PIN_MAX_LENGTH = 6

export type { FieldErrors }

export function validateDisplayName(displayName: string): string | undefined {
  const trimmed = displayName.trim()

  if (trimmed === '') {
    return 'A name is required.'
  }

  if (trimmed.length > DISPLAY_NAME_MAX_LENGTH) {
    return `A name is at most ${String(DISPLAY_NAME_MAX_LENGTH)} characters.`
  }

  return undefined
}

/**
 * Whether an email is worth sending to the server.
 *
 * Deliberately shallow — a single `@` with something either side. Email
 * validation by regex is a well-known way to reject addresses that work, and
 * the only authority on whether an address is real is whether mail reaches it,
 * which nothing here can check. The server applies the same rule.
 */
export function validateEmail(email: string): string | undefined {
  const trimmed = email.trim()

  if (trimmed === '') {
    return 'An email address is required.'
  }

  if (trimmed.length > EMAIL_MAX_LENGTH) {
    return `An email address is at most ${String(EMAIL_MAX_LENGTH)} characters.`
  }

  if (!/^[^@\s]+@[^@\s]+$/.test(trimmed)) {
    return 'That does not look like an email address.'
  }

  return undefined
}

/** `Pin.IsWellFormed`: 4 to 6 ASCII digits, and nothing else. */
export function validatePin(pin: string): string | undefined {
  if (!new RegExp(`^\\d{${String(PIN_MIN_LENGTH)},${String(PIN_MAX_LENGTH)}}$`).test(pin)) {
    return `A PIN is ${String(PIN_MIN_LENGTH)} to ${String(PIN_MAX_LENGTH)} digits.`
  }

  return undefined
}

export function validateNewEmployee(values: {
  displayName: string
  email: string
  password: string
  pin: string
}): FieldErrors {
  const errors: FieldErrors = {}

  const displayName = validateDisplayName(values.displayName)
  if (displayName !== undefined) {
    errors['displayName'] = displayName
  }

  const email = validateEmail(values.email)
  if (email !== undefined) {
    errors['email'] = email
  }

  if (values.password === '') {
    errors['password'] = 'A password is required.'
  }

  // Optional. A cashier who only ever uses the till needs one; a bookkeeper who
  // never touches it does not, and forcing a PIN on them puts a working
  // credential on the PIN screen for no reason.
  if (values.pin !== '') {
    const pin = validatePin(values.pin)
    if (pin !== undefined) {
      errors['pin'] = pin
    }
  }

  return errors
}

export function validateExistingEmployee(values: { displayName: string }): FieldErrors {
  const errors: FieldErrors = {}

  const displayName = validateDisplayName(values.displayName)
  if (displayName !== undefined) {
    errors['displayName'] = displayName
  }

  return errors
}
