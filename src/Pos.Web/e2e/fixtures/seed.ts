import { execFileSync } from 'node:child_process'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * The database and credentials the E2E run works against.
 *
 * A **dedicated `pos_e2e` database**, not the dev one. Two reasons, and the
 * second is the important one:
 *
 * 1. A test run that creates products should not accumulate them in the
 *    database somebody is clicking around in.
 * 2. Dev databases seeded before the Phase 3 branch carry stock drift —
 *    `tools/Pos.Seed` used to write a stock row with an opening balance and no
 *    matching movement, so `OnHand != SUM(movements)`. A fresh database has
 *    never had that, so the ledger assertions here mean what they say.
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..')

const HOST = process.env['POSTGRES_HOST'] ?? 'localhost'
const PORT = process.env['POSTGRES_PORT'] ?? '5432'
const DATABASE = 'pos_e2e'

/**
 * The local Compose default, and the only place this file names one.
 *
 * `docker-compose.yml` publishes the same value the same way —
 * `${POSTGRES_APP_PASSWORD:-dev_only_not_a_secret}` — so a machine that has
 * overridden it works here without editing anything, and CI or any non-default
 * setup supplies its own through the environment rather than relying on this.
 *
 * It is a throwaway on exactly the terms `docker-compose.yml` documents: it
 * binds to localhost on a dev machine, or to a CI service container that lives
 * for one job. No real secret is ever this value. Secret scanning still flags
 * the shape, which is the scanner working correctly — see docs/HANDOFF.md.
 */
const COMPOSE_DEFAULT_PASSWORD = 'dev_only_not_a_secret'

const OWNER_PASSWORD = process.env['POSTGRES_PASSWORD'] ?? COMPOSE_DEFAULT_PASSWORD
const APP_PASSWORD = process.env['POSTGRES_APP_PASSWORD'] ?? COMPOSE_DEFAULT_PASSWORD

function connectionFor(username: string, password: string): string {
  return `Host=${HOST};Port=${PORT};Database=${DATABASE};Username=${username};Password=${password}`
}

/** The schema owner. Migrations only — it is a superuser and bypasses RLS. */
export const OWNER_CONNECTION = connectionFor('pos', OWNER_PASSWORD)

/** What the API connects as: NOBYPASSRLS, no CREATE on the schema. */
export const APP_CONNECTION = connectionFor('pos_app', APP_PASSWORD)

export const TENANT_SLUG = 'e2e-shop'
export const CASHIER_PIN = '4821'

/**
 * The manager's PIN and the name the PIN pad lists them under.
 *
 * `SeedOptions.DefaultManagerPin` and the display name `DevSeeder` gives the
 * manager. The name is needed because the override dialog lists staff exactly as
 * the PIN screen does — by display name, never by id, because that is all
 * `GET /employees/pin-eligible` returns.
 */
export const MANAGER_PIN = '7391'
export const MANAGER_NAME = 'Sam Cole'

/**
 * The seeded users' password.
 *
 * Satisfies the Identity rules in `AddPosIdentity` (10+, upper, lower, digit)
 * and matches `SeedOptions.DefaultPassword`. Same terms as above: it belongs to
 * three fake users in a throwaway database.
 */
export const PASSWORD = process.env['POS_SEED_PASSWORD'] ?? 'Dev-Password-1'

export const OWNER_EMAIL = `owner@${TENANT_SLUG}.test`
export const MANAGER_EMAIL = `manager@${TENANT_SLUG}.test`
export const CASHIER_EMAIL = `cashier@${TENANT_SLUG}.test`

/**
 * What the specs cannot get any other way.
 *
 * Only these two. A user id is deliberately not captured: the PIN screen lists
 * staff by display name, so a spec that knew the id would be testing something
 * no cashier ever does.
 */
export interface SeedResult {
  /** Shown once, at enrolment. `--rotate-device-token` is what makes it printable again. */
  deviceToken: string
  registerId: string
}

const SEED_FILE = resolve(dirname(fileURLToPath(import.meta.url)), '../.seed.json')

/**
 * Seeds `pos_e2e`, returning what the tests cannot get any other way.
 *
 * **Migration is not done here.** Playwright starts its `webServer` entries
 * before `globalSetup` runs, so a migration in this function would happen after
 * the API had already tried to reach a database that did not exist. The
 * migration is chained into the API's own command in `playwright.config.ts`;
 * this runs once the API is up, which is exactly when the schema is guaranteed
 * to be there.
 *
 * Nothing here needs `psql`, which matters because CI talks to a Postgres
 * *service* rather than the local Compose container.
 */
export function seedDatabase(): SeedResult {
  // `--rotate-device-token` unconditionally: a device token is shown only at
  // enrolment, so a re-run against an already-seeded database could not reprint
  // the previous one and the PIN specs would have nothing to present.
  const output = run('dotnet', [
    'run',
    '--project',
    'tools/Pos.Seed',
    '--',
    '--connection',
    APP_CONNECTION,
    '--slug',
    TENANT_SLUG,
    '--name',
    'E2E Shop',
    '--password',
    PASSWORD,
    '--cashier-pin',
    CASHIER_PIN,
    '--rotate-device-token',
  ])

  const result: SeedResult = {
    deviceToken: capture(output, /X-Device-Token:\s*(\S+)/, 'the device token'),
    // The status column is "enrolled" or "not enrolled" — one token or two.
    registerId: capture(
      output,
      /Front Counter\s+(?:not\s+)?enrolled\s+id\s+([0-9a-f-]{36})/i,
      "the register's id",
    ),
  }

  mkdirSync(dirname(SEED_FILE), { recursive: true })
  writeFileSync(SEED_FILE, JSON.stringify(result, null, 2))

  return result
}

/** Reads what `seedDatabase` wrote. Specs use this rather than re-seeding. */
export function readSeed(): SeedResult {
  return JSON.parse(readFileSync(SEED_FILE, 'utf8')) as SeedResult
}

function run(command: string, args: string[]): string {
  return execFileSync(command, args, {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'inherit'],
    // A cold `dotnet ef` restore-and-build is slow the first time.
    timeout: 300_000,
  })
}

function capture(output: string, pattern: RegExp, what: string): string {
  const match = pattern.exec(output)

  if (match?.[1] === undefined) {
    // Loudly, with the output attached: the alternative is a spec failing later
    // with "expected 200, got 401" and no clue that seeding was the problem.
    throw new Error(`Could not read ${what} from the seeder's output:\n\n${output}`)
  }

  return match[1]
}
