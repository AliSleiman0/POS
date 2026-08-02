import { defineConfig, devices } from '@playwright/test'
import { APP_CONNECTION, OWNER_CONNECTION } from './e2e/fixtures/seed'

/**
 * E2E configuration.
 *
 * These tests run against the **real API and a real Postgres**, not mocks. A
 * mocked E2E test only proves the frontend agrees with the mock — which is
 * precisely the drift the generated client exists to prevent, so mocking here
 * would give the whole arrangement away.
 *
 * Two servers are started: the API on 5013 and Vite on 5173. The browser only
 * ever talks to Vite, which proxies `/api` to the API — one origin, no CORS,
 * and the same shape production has behind a single domain.
 *
 * Browsers are NOT installed by `pnpm install`. Run `pnpm exec playwright
 * install chromium` first (add `--with-deps` on Linux; it is a no-op that hangs
 * on Windows).
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 2 : 0,
  workers: process.env['CI'] ? 1 : undefined,
  reporter: process.env['CI'] ? 'blob' : 'html',

  // Seeds the tenant, staff and register. Runs *after* the servers are up —
  // Playwright launches `webServer` first — which is why the **migration** is
  // chained into the API's own command below rather than living here. The API
  // would otherwise boot against a database that does not exist yet and sit at
  // an unhealthy readiness check until the timeout.
  globalSetup: './e2e/global-setup.ts',

  use: {
    baseURL: 'http://localhost:5173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },

  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],

  webServer: [
    {
      // Migrate, then serve. `dotnet ef database update` creates `pos_e2e` when
      // it does not exist, and the RowLevelSecurity migration grants `pos_app`
      // the privileges it needs — so the app role can use a database it did not
      // exist for. Migrations connect as the schema **owner**: `pos_app` is
      // NOBYPASSRLS with no CREATE on the schema, and would fail with
      // "permission denied for schema public".
      command:
        `dotnet ef database update --project ../Pos.Data --startup-project ../Pos.Api --connection "${OWNER_CONNECTION}"` +
        ' && dotnet run --project ../Pos.Api --no-launch-profile',
      url: 'http://localhost:5013/health/ready',
      reuseExistingServer: false,
      timeout: 180_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        // 5013 explicitly: --no-launch-profile means launchSettings.json is not
        // read, so ASPNETCORE_URLS is honoured here even though `dotnet run`
        // normally ignores it.
        ASPNETCORE_URLS: 'http://localhost:5013',
        ConnectionStrings__Postgres: APP_CONNECTION,
        // Supplied rather than taken from user-secrets: CI has none, and a
        // deploy with no usable signing key must refuse to start (ValidateOnStart).
        // A throwaway on the same terms as the compose passwords.
        Jwt__SigningKey: 'e2e-only-signing-key-not-a-secret-0123456789abcdef',
        Jwt__Issuer: 'pos-e2e',
        Jwt__Audience: 'pos-e2e',
      },
    },
    {
      command: 'pnpm dev',
      url: 'http://localhost:5173',
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
  ],
})
