import type { Page } from '@playwright/test'
import { CASHIER_EMAIL, MANAGER_EMAIL, OWNER_EMAIL, PASSWORD, readSeed, TENANT_SLUG } from './seed'

export type Role = 'owner' | 'manager' | 'cashier'

const EMAILS: Record<Role, string> = {
  owner: OWNER_EMAIL,
  manager: MANAGER_EMAIL,
  cashier: CASHIER_EMAIL,
}

/**
 * Signs in through the real login form.
 *
 * Deliberately through the UI rather than by injecting a token: the login form
 * and the token plumbing behind it are part of what Phase 4.2 has to prove, and
 * a fixture that bypassed them would leave the app's own entry point untested.
 */
export async function signIn(page: Page, role: Role): Promise<void> {
  await page.goto('/login')

  await page.getByLabel('Shop').fill(TENANT_SLUG)
  await page.getByLabel('Email').fill(EMAILS[role])
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Sign in' }).click()

  await page.waitForURL('/')
}

/**
 * Makes this browser a till, without going through the enrolment screen.
 *
 * Every test gets a fresh browser context — `describe.configure({ mode:
 * 'serial' })` shares the *worker*, not storage — so a spec that needs an
 * enrolled device has to arrange one for itself. The enrolment UI has its own
 * spec; this is for the specs that are about something else.
 *
 * `addInitScript` rather than `page.evaluate`, so the keys are in place before
 * the app's first render reads them.
 */
export async function enrolDevice(page: Page): Promise<void> {
  const { deviceToken, registerId } = readSeed()

  await page.addInitScript(
    ([token, register]) => {
      localStorage.setItem('pos.deviceToken', token as string)
      localStorage.setItem('pos.registerId', register as string)
    },
    [deviceToken, registerId],
  )
}

/** A SKU no other test or run will collide with. */
export function uniqueSku(prefix: string): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`.toUpperCase()
}

/**
 * An email no other test or run will collide with.
 *
 * `pos_e2e` is seeded once and never dropped, so every run that creates a user
 * leaves it behind — the staff list only grows. Nothing may assert a count or
 * expect a row in a particular position; find people by the address that only
 * this run used.
 */
export function uniqueEmail(prefix: string): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}@e2e.test`
}

/** A barcode digit string, unique for the same reason. */
export function uniqueBarcode(): string {
  return `9${Date.now().toString().slice(-9)}${Math.floor(Math.random() * 1000)
    .toString()
    .padStart(3, '0')}`
}
