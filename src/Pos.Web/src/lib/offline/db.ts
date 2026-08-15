/**
 * The till's local database: the catalog it sells from and the sales it owes
 * the server.
 *
 * **One database per tenant**, named `pos-offline-<slug>`. Not a
 * convenience — a shared counter tablet can be signed into one shop and then
 * another, and a single database would let the second read the first's prices
 * and, far worse, replay the first's queued sales into it. Keying on the tenant
 * makes that impossible rather than merely unlikely.
 *
 * **Why IndexedDB and not `sessionStorage`**, which is where the cart lives.
 * The two hold different things and want opposite lifetimes:
 *
 * | | Where | Survives a browser close? |
 * |---|---|---|
 * | Cart | `sessionStorage` | **No** — a shared till must not hand the next shift the last one's basket |
 * | Refresh token | `sessionStorage` | **No** — invariant 11; a credential on a shared device dies with the tab |
 * | Catalog mirror | IndexedDB | Yes — it is public shop data and rebuilding it costs a download |
 * | Outbox | IndexedDB | **Yes, and this is the point** — a queued sale is money that has already changed hands |
 *
 * That last row is the reconciliation invariant 11 asks for. Losing a cart on a
 * tab close is the correct direction to fail; losing a sale a customer has
 * already paid for is not, and no amount of shared-device hygiene makes it so.
 * The credential rule is untouched — nothing here holds one.
 *
 * **Everything read back out is untrusted input**, exactly as
 * `features/register/storage.ts` treats its two keys. It is data a user could
 * have edited, from a build that may not be this one.
 */

import { openDB, deleteDB, type DBSchema, type IDBPDatabase } from 'idb'

/**
 * Bumped when any store's shape changes.
 *
 * **An unrecognised version is dropped, not migrated** — the whole database is
 * deleted and rebuilt from the server. The same rule `storage.ts` states for the
 * cart, and it costs more here, so it is worth restating: a mirror is
 * re-downloadable and a bad migration is a till that crashes on load, which is
 * unfixable by the only remedy a shop floor has.
 *
 * **The outbox is the exception, and it is not handled yet.** Dropping the
 * mirror costs a download; dropping the outbox discards sales a customer has
 * paid for. There is no version 2, so the upgrade path below has never run —
 * writing a rescue for a migration that does not exist would be untested code
 * on the one path that must not be. Whoever bumps this owes the queue a
 * migration: read `outbox` out before the stores are dropped and write it back
 * after, or drain it to the server first and refuse to upgrade until it is
 * empty. Either is fine. Losing it is not.
 */
const VERSION = 1

/** A product, as the mirror holds it. Shapes mirror `GET /catalog/sync`. */
export interface MirroredProduct {
  id: string
  sku: string
  name: string
  description: string | null
  categoryId: string | null
  taxClassId: string
  /** A decimal string, never a JS number — see `lib/pricing`. */
  unitPrice: string
  unit: string
  isActive: boolean
  trackStock: boolean
  /**
   * Whether this is a modifier — "extra cheese" — rather than something sold
   * on its own.
   *
   * Mirrored so the offline grid and the offline search exclude them, exactly
   * as the online ones do. A till that offered "extra cheese" as a scannable
   * line while the network was down would sell one, and the sale would be
   * perfectly valid.
   */
  isModifier: boolean
  /** Lower-cased name, for prefix search without scanning every row. */
  search: string
}

export interface MirroredBarcode {
  /** The scanned value. **The key**, because that is how it is looked up. */
  code: string
  id: string
  productId: string
  isPrimary: boolean
}

export interface MirroredTaxClass {
  id: string
  name: string
  /** A decimal string: `0.2300` is 23%. */
  rate: string
  isDefault: boolean
}

export interface MirroredCategory {
  id: string
  name: string
  parentCategoryId: string | null
  sortOrder: number
  isActive: boolean
}

export interface MirroredSettings {
  currencyCode: string
  timeZoneId: string
  taxMode: 'Inclusive' | 'Exclusive'
  /**
   * Which front-of-house model this shop runs.
   *
   * Mirrored so a till that has lost the network still knows what it is. It
   * enables nothing offline — an order lives on the server, so a restaurant till
   * cannot take one with the line down — it is how the app can *say so* instead
   * of showing a floor plan that refuses every tap.
   */
  serviceMode: 'Retail' | 'Restaurant'
  cashRoundingIncrement: string
  businessDayStartOffset: string
  addressLine: string | null
  taxNumber: string | null
  receiptHeader: string | null
  receiptFooter: string | null
}

/** Where the mirror got to, and when. */
export interface MirrorMeta {
  /** The server's watermark from the last completed sync. */
  watermark: string | null
  /** Epoch ms of the last completed sync — what "prices as of 09:14" reads. */
  syncedAt: number | null
}

/**
 * A sale the till has taken and the server has not acknowledged.
 *
 * Written **before** any network attempt, so the record exists in exactly the
 * case that needs it. Defined here rather than in `outbox.ts` because the
 * schema is one thing.
 */
export interface OutboxSale {
  /** The `clientTransactionId` and `Idempotency-Key`. The key of this store. */
  saleKey: string
  /**
   * The tenant that owns it. Replay refuses under any other.
   *
   * The **slug**, not a GUID: `GET /auth/me` returns the tenant as a slug, a
   * name and a currency, and no id — so the slug is the only stable tenant
   * identity a signed-in client actually holds. It is unique per tenant and no
   * route changes it.
   */
  tenantKey: string
  /** The till that took it. Replay refuses under any other. */
  registerId: string
  shiftId: string
  /**
   * When the customer paid, ISO-8601.
   *
   * **Minted once, here, and never re-read from the clock.** It is part of the
   * request body and therefore part of the idempotency fingerprint: a retry
   * carrying a fresh timestamp is a different body under the same key, and the
   * server answers `409 idempotency-key-reused` for ever.
   */
  occurredAt: string
  /** The `POST /sales` body, complete and ready to send unchanged. */
  body: unknown
  /** What the cashier was shown, so a receipt can be reprinted offline. */
  display: OutboxDisplay
  status: 'pending' | 'failed'
  attempts: number
  /** Epoch ms. The replay loop leaves it alone until then. */
  nextAttemptAt: number
  /** Why the last attempt failed, for the review queue. */
  lastError: string | null
  /** A stable problem slug when the failure was permanent. */
  lastErrorType: string | null
  createdAt: number
}

/** Enough of the sale to print a receipt and to explain it in a review queue. */
export interface OutboxDisplay {
  lines: { description: string; quantity: string; unitPrice: string; lineTotal: string }[]
  subtotal: string
  taxTotal: string
  discountTotal: string
  roundingAdjustment: string
  total: string
  tendered: string
  change: string
  currencyCode: string
}

interface OfflineSchema extends DBSchema {
  products: {
    key: string
    value: MirroredProduct
    indexes: { search: string }
  }
  barcodes: {
    key: string
    value: MirroredBarcode
    /**
     * `id` as well as `productId`, because the two are asked different
     * questions. The store is keyed by *code* — a scan is a point lookup — but a
     * withdrawal arrives as an **id**, and the code is precisely what the server
     * no longer sends for a removed row. Without this index each tombstone costs
     * a scan of every barcode the shop has.
     */
    indexes: { productId: string; id: string }
  }
  taxClasses: { key: string; value: MirroredTaxClass }
  categories: { key: string; value: MirroredCategory }
  /** One row, under the key `current`. */
  settings: { key: string; value: MirroredSettings }
  /** One row, under the key `mirror`. */
  meta: { key: string; value: MirrorMeta }
  outbox: {
    key: string
    value: OutboxSale
    indexes: { status: string }
  }
}

export type OfflineDb = IDBPDatabase<OfflineSchema>

/** Open handles, one per tenant, so a page does not reopen on every read. */
const open = new Map<string, Promise<OfflineDb>>()

export function databaseName(tenantKey: string): string {
  return `pos-offline-${tenantKey}`
}

/**
 * Opens (and creates) this tenant's database.
 *
 * @throws Never. A browser with IndexedDB disabled — Safari in private mode, a
 * locked-down webview — must still be able to sell online, so callers get a
 * rejected promise and degrade rather than a blank screen.
 */
export function openOfflineDb(tenantKey: string): Promise<OfflineDb> {
  const name = databaseName(tenantKey)

  let handle = open.get(name)

  if (handle === undefined) {
    handle = openDB<OfflineSchema>(name, VERSION, {
      upgrade(db, oldVersion) {
        /*
         * A version this build does not know: start again.
         *
         * `storage.ts` states the rule for the cart and it holds here — the
         * cost of a bad migration is a till that cannot be recovered by
         * reloading, and reloading is the only remedy a shop floor has. The
         * mirror is re-downloadable, so dropping it costs a sync.
         *
         * Deleting stores rather than the database, because `upgrade` runs
         * inside the open that would recreate it.
         */
        if (oldVersion > 0) {
          /*
           * Dead code today — VERSION is 1, so `oldVersion` is always 0 here.
           *
           * **Read the note on VERSION before making it live.** Dropping the
           * stores discards the outbox, and the outbox holds sales customers
           * have already paid for. A version bump needs that queue migrated or
           * drained first; this branch as written is correct for the mirror and
           * unacceptable for the queue.
           */
          for (const store of db.objectStoreNames) {
            db.deleteObjectStore(store)
          }
        }

        const products = db.createObjectStore('products', { keyPath: 'id' })
        products.createIndex('search', 'search')

        // Keyed by the code itself: a scan is a point lookup, and it is the
        // hottest read in the application.
        const barcodes = db.createObjectStore('barcodes', { keyPath: 'code' })
        barcodes.createIndex('productId', 'productId')
        barcodes.createIndex('id', 'id', { unique: true })

        db.createObjectStore('taxClasses', { keyPath: 'id' })
        db.createObjectStore('categories', { keyPath: 'id' })
        db.createObjectStore('settings')
        db.createObjectStore('meta')

        const outbox = db.createObjectStore('outbox', { keyPath: 'saleKey' })
        outbox.createIndex('status', 'status')
      },

      blocked() {
        // Another tab holds an older version open. Nothing to do but wait; the
        // till in front of the cashier is the one that matters.
      },
    })

    open.set(name, handle)
  }

  return handle
}

/**
 * Forgets this tenant's mirror entirely.
 *
 * **Not called on sign-out**, deliberately. The outbox lives in here, and a
 * cashier signing off at the end of a shift must not take the shop's unsynced
 * takings with them. It exists for the diagnostics screen, where somebody has
 * decided to rebuild a mirror they believe is wrong — and 9.5's copy says
 * plainly what that costs if anything is still queued.
 */
export async function deleteOfflineDb(tenantKey: string): Promise<void> {
  const name = databaseName(tenantKey)

  const handle = open.get(name)
  open.delete(name)

  if (handle !== undefined) {
    ;(await handle).close()
  }

  await deleteDB(name)
}

/** Test seam. Drops the cached handles without touching the data. */
export function __closeOfflineDbs(): void {
  for (const handle of open.values()) {
    void handle.then((db) => {
      db.close()
    })
  }

  open.clear()
}
