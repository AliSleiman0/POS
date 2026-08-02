import { seedDatabase } from './fixtures/seed'

/**
 * Brings the E2E database up before Playwright starts the servers.
 *
 * Runs once per run, not per worker. Playwright starts the `webServer` entries
 * *after* this resolves, so the API is guaranteed to find a migrated database
 * rather than racing the migration.
 */
export default function globalSetup(): void {
  const seed = seedDatabase()

  // eslint-disable-next-line no-console
  console.log(`e2e: seeded pos_e2e — register ${seed.registerId}`)
}
