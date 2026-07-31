import { defineConfig, devices } from '@playwright/test'

/**
 * E2E configuration. Specs arrive in Phase 4 (login → catalog) and Phase 5
 * (scan → cart → tender).
 *
 * Browsers are NOT installed by `pnpm install` — run `pnpm exec playwright
 * install --with-deps` before the first e2e run. Deferred deliberately: it is a
 * ~500 MB download that nothing needs until Phase 4.
 *
 * These tests run against the real API and a real Postgres, not mocks. A mocked
 * E2E test only proves the frontend agrees with the mock.
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 2 : 0,
  workers: process.env['CI'] ? 1 : undefined,
  reporter: process.env['CI'] ? 'blob' : 'html',

  use: {
    baseURL: 'http://localhost:5173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },

  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],

  webServer: {
    command: 'pnpm dev',
    url: 'http://localhost:5173',
    reuseExistingServer: !process.env['CI'],
    timeout: 120_000,
  },
})
