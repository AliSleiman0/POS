/**
 * The catalog mirror: what the till sells from when it cannot ask the server.
 *
 * **Barcode lookup goes here first, always — online as well as offline.** That
 * is 9.2's decision and it is not only about the network being down: a scan is
 * the hottest read in the application, and a till that pays a round trip per
 * item is slow on precisely the connection a shop has. The server is the
 * fallback for a code the mirror has not seen, which is also how a mirror that
 * is behind heals itself.
 *
 * **What this file does not do is decide prices.** It stores the decimal
 * strings the server sent and hands them to `lib/pricing`, which is the only
 * thing that turns them into amounts.
 */

import type {
  MirroredBarcode,
  MirroredCategory,
  MirroredProduct,
  MirroredSettings,
  MirroredTaxClass,
  MirrorMeta,
  OfflineDb,
} from './db'

const META_KEY = 'mirror'
const SETTINGS_KEY = 'current'

/** One page of `GET /catalog/sync`, as the mirror consumes it. */
export interface MirrorPage {
  products: MirroredProduct[]
  barcodes: MirroredBarcode[]
  removedBarcodeIds: string[]
  taxClasses: MirroredTaxClass[]
  categories: MirroredCategory[]
  settings: MirroredSettings | null
}

/**
 * Applies one page, in a single transaction.
 *
 * **Upserts throughout**, which is what makes the feed's one permitted failure
 * harmless: `GET /catalog/sync` may serve a row twice — a product edited while a
 * till is mid-walk moves ahead of the cursor — and re-receiving it costs a put.
 * It can never skip a row, so there is nothing to reconcile in the other
 * direction.
 *
 * One transaction per page rather than per row, so a page either lands or does
 * not. A half-applied page is a mirror holding a product whose tax class it has
 * not got, which prices its lines at nothing.
 */
export async function applyPage(db: OfflineDb, page: MirrorPage): Promise<void> {
  const tx = db.transaction(
    ['products', 'barcodes', 'taxClasses', 'categories', 'settings'],
    'readwrite',
  )

  const products = tx.objectStore('products')
  const barcodes = tx.objectStore('barcodes')

  await Promise.all([
    ...page.products.map((product) => products.put(product)),
    ...page.taxClasses.map((taxClass) => tx.objectStore('taxClasses').put(taxClass)),
    ...page.categories.map((category) => tx.objectStore('categories').put(category)),
    ...page.barcodes.map((barcode) => barcodes.put(barcode)),

    /*
     * Withdrawals, by id rather than by code.
     *
     * The store is keyed by code — a scan is a point lookup — but a withdrawal
     * arrives as an id, and the code is exactly what the server no longer sends
     * for a removed row. Hence the `id` index: without it each tombstone is a
     * scan of every barcode the shop has, which on a ten-thousand-product
     * catalog is the sync stalling the UI the phase doc says it must not.
     *
     * A tombstone for a code the mirror never had is a no-op, which is the
     * ordinary case on a till that was offline when the code was both added and
     * withdrawn.
     */
    ...page.removedBarcodeIds.map(async (id) => {
      const existing = await barcodes.index('id').getKey(id)

      if (existing !== undefined) {
        await barcodes.delete(existing)
      }
    }),

    page.settings === null
      ? Promise.resolve()
      : tx.objectStore('settings').put(page.settings, SETTINGS_KEY),
  ])

  await tx.done
}

/** Records where the mirror got to. Written only after a full walk. */
export async function commitWatermark(
  db: OfflineDb,
  watermark: string,
  syncedAt: number,
): Promise<void> {
  await db.put('meta', { watermark, syncedAt }, META_KEY)
}

export async function readMeta(db: OfflineDb): Promise<MirrorMeta> {
  const stored = await db.get('meta', META_KEY)

  // Defensive, like everything read back out of storage: a shape this build
  // does not recognise is treated as "never synced" rather than trusted.
  if (
    typeof stored !== 'object' ||
    stored === null ||
    !(typeof stored.watermark === 'string' || stored.watermark === null)
  ) {
    return { watermark: null, syncedAt: null }
  }

  return {
    watermark: stored.watermark,
    syncedAt: typeof stored.syncedAt === 'number' ? stored.syncedAt : null,
  }
}

/**
 * The product a scanned code belongs to, with its tax rate — everything a cart
 * line needs, in one read.
 *
 * `null` when the mirror does not know the code, which the caller answers by
 * asking the server. Offline, `null` is "unknown item" and the register says so.
 */
export async function lookupBarcode(db: OfflineDb, code: string): Promise<MirroredScan | null> {
  const barcode = await db.get('barcodes', code.trim())

  if (barcode === undefined) {
    return null
  }

  const product = await db.get('products', barcode.productId)

  if (product === undefined) {
    // A barcode whose product the mirror has not got. Possible mid-sync, and
    // answering with a priceless line would be worse than saying nothing.
    return null
  }

  const taxClass = await db.get('taxClasses', product.taxClassId)

  if (taxClass === undefined) {
    return null
  }

  return { product, taxRate: taxClass.rate, taxClassName: taxClass.name }
}

/** What a scan resolves to locally. */
export interface MirroredScan {
  product: MirroredProduct
  taxRate: string
  taxClassName: string
}

/**
 * Products whose name or SKU matches, for the grid's search box.
 *
 * **Bounded by `limit` and cursored rather than loaded whole.** A ten-thousand
 * product catalog read into an array on every keystroke is a stalled UI on the
 * device this most needs to work on — the phase doc asks for exactly that not to
 * happen.
 *
 * The `search` index holds the lower-cased name, so a prefix match is a range
 * scan. A *contains* match still costs a walk, which is why the prefix hits are
 * taken first and the walk stops as soon as the page is full.
 */
export async function searchProducts(
  db: OfflineDb,
  term: string,
  limit = 24,
): Promise<MirroredProduct[]> {
  const needle = term.trim().toLowerCase()

  if (needle.length === 0) {
    return activeProducts(db, limit)
  }

  const found = new Map<string, MirroredProduct>()

  // Prefix first, off the index: "wat" finds "Water" without reading anything
  // that does not start with it.
  const prefix = IDBKeyRange.bound(needle, `${needle}￿`)

  for await (const cursor of db.transaction('products').store.index('search').iterate(prefix)) {
    if (cursor.value.isActive && !cursor.value.isModifier) {
      found.set(cursor.value.id, cursor.value)
    }

    if (found.size >= limit) {
      return [...found.values()]
    }
  }

  // Then a contains/SKU walk for the rest of the page. Bounded by the same
  // limit, so the cost is capped however large the catalog is.
  for await (const cursor of db.transaction('products').store.iterate()) {
    const product = cursor.value

    if (
      product.isActive &&
      !product.isModifier &&
      !found.has(product.id) &&
      (product.search.includes(needle) || product.sku.toLowerCase() === needle)
    ) {
      found.set(product.id, product)
    }

    if (found.size >= limit) {
      break
    }
  }

  return [...found.values()]
}

async function activeProducts(db: OfflineDb, limit: number): Promise<MirroredProduct[]> {
  const found: MirroredProduct[] = []

  for await (const cursor of db.transaction('products').store.index('search').iterate()) {
    if (cursor.value.isActive && !cursor.value.isModifier) {
      found.push(cursor.value)
    }

    if (found.length >= limit) {
      break
    }
  }

  return found
}

export async function readSettings(db: OfflineDb): Promise<MirroredSettings | null> {
  return (await db.get('settings', SETTINGS_KEY)) ?? null
}

export async function readTaxClass(
  db: OfflineDb,
  id: string,
): Promise<MirroredTaxClass | undefined> {
  return db.get('taxClasses', id)
}

/** How many products the mirror holds. Shown on the diagnostics screen. */
export async function mirrorSize(db: OfflineDb): Promise<number> {
  return db.count('products')
}
