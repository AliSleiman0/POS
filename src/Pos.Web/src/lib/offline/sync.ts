/**
 * Driving `GET /catalog/sync` into the mirror.
 *
 * **The watermark is committed only after a complete walk.** Both cursors have
 * to come back null first. Committing after the first page would move the
 * mirror's mark past rows it never fetched, and — because the next sync asks
 * only for what changed *since* the mark — those rows would never arrive. Not a
 * delay: a permanent hole, in a till's prices, that nothing would report.
 *
 * The overlap on the way back out is the other half. A row's changed-at is
 * stamped when the write runs but becomes visible when its transaction commits,
 * so a write that began before the watermark can surface after it carrying an
 * earlier timestamp. Re-asking from slightly before costs nothing because every
 * write here is an upsert.
 */

import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'
import { applyPage, commitWatermark, readMeta, type MirrorPage } from './catalog'
import type {
  MirroredBarcode,
  MirroredCategory,
  MirroredProduct,
  MirroredSettings,
  MirroredTaxClass,
  OfflineDb,
} from './db'

type SyncResponse = components['schemas']['CatalogSyncResponse']
type ServerDecimal = number | string

/**
 * How far back to rewind. Mirrors `CatalogSyncEndpoints.WatermarkOverlapSeconds`
 * — the server advertises it so a client does not have to invent one.
 */
const OVERLAP_MS = 30_000

/**
 * A ceiling on pages per run, so a bug cannot spin.
 *
 * At the default limit this is two hundred thousand rows, which is far past any
 * catalog this product targets. It exists because the loop's exit depends on the
 * server returning a null cursor, and "trust the other end to terminate the
 * loop" is not a property worth relying on in code that runs on a till.
 */
const MAX_PAGES = 1_000

export interface SyncResult {
  pages: number
  products: number
  barcodes: number
  removed: number
  /** The watermark now committed, or null when the walk did not finish. */
  watermark: string | null
}

/**
 * Brings the mirror up to date.
 *
 * @throws whatever the client throws. Offline, that is a network error, and the
 * caller treats a failed sync as "the mirror is as old as it was" — never as a
 * reason to empty it.
 */
export async function syncCatalog(db: OfflineDb): Promise<SyncResult> {
  const meta = await readMeta(db)

  const since =
    meta.watermark === null
      ? undefined
      : new Date(Date.parse(meta.watermark) - OVERLAP_MS).toISOString()

  let productCursor: string | undefined
  let barcodeCursor: string | undefined
  let watermark: string | null = null

  const counted = { pages: 0, products: 0, barcodes: 0, removed: 0 }

  for (let page = 0; page < MAX_PAGES; page++) {
    const body: SyncResponse = await unwrap(
      api.GET('/api/v1/catalog/sync', {
        params: {
          query: {
            since,
            productCursor,
            barcodeCursor,
          },
        },
      }),
    )

    await applyPage(db, toMirrorPage(body))

    counted.pages += 1
    counted.products += body.products.length
    counted.barcodes += body.barcodes.length
    counted.removed += body.removedBarcodeIds.length

    // The server's, always. A till whose clock is fast would skip everything
    // changed in the gap between the two clocks.
    watermark = body.watermark

    productCursor = body.nextProductCursor ?? undefined
    barcodeCursor = body.nextBarcodeCursor ?? undefined

    if (productCursor === undefined && barcodeCursor === undefined) {
      await commitWatermark(db, watermark, Date.now())

      return { ...counted, watermark }
    }

    /*
     * Yield between pages.
     *
     * A ten-thousand-product catalog is fifty pages of IndexedDB writes, and
     * doing them back to back holds the main thread long enough for a scan to
     * be dropped — `useScanner` tells a wedge scanner from a person by timing a
     * burst, so a stalled frame does not merely delay a scan, it splits one in
     * half and looks up its tail. The phase doc's "without stalling the UI" is
     * this line.
     */
    await new Promise((resolve) => {
      setTimeout(resolve, 0)
    })
  }

  // Ran out of pages without the server saying it was done. The watermark is
  // deliberately NOT committed: the mirror is incomplete and the next sync must
  // start from where it did, not from where it stopped.
  return { ...counted, watermark: null }
}

function toMirrorPage(body: SyncResponse): MirrorPage {
  return {
    products: body.products.map(toProduct),
    barcodes: body.barcodes.map(toBarcode),
    removedBarcodeIds: body.removedBarcodeIds,
    taxClasses: body.taxClasses.map(toTaxClass),
    categories: body.categories.map(toCategory),
    settings: body.settings === null ? null : toSettings(body.settings),
  }
}

function toProduct(row: SyncResponse['products'][number]): MirroredProduct {
  return {
    id: row.id,
    sku: row.sku,
    name: row.name,
    description: row.description ?? null,
    categoryId: row.categoryId ?? null,
    taxClassId: row.taxClassId,
    unitPrice: decimal(row.unitPrice),
    unit: row.unit,
    isActive: row.isActive,
    trackStock: row.trackStock,
    // Stored lower-cased so the search index is a range scan rather than a
    // case-folding walk over every row on every keystroke.
    search: row.name.toLowerCase(),
  }
}

function toBarcode(row: SyncResponse['barcodes'][number]): MirroredBarcode {
  return {
    code: row.code,
    id: row.id,
    productId: row.productId,
    isPrimary: row.isPrimary,
  }
}

function toTaxClass(row: SyncResponse['taxClasses'][number]): MirroredTaxClass {
  return {
    id: row.id,
    name: row.name,
    rate: decimal(row.rate),
    isDefault: row.isDefault,
  }
}

function toCategory(row: SyncResponse['categories'][number]): MirroredCategory {
  return {
    id: row.id,
    name: row.name,
    parentCategoryId: row.parentCategoryId ?? null,

    /*
     * Coerced, because .NET's OpenAPI types an `int32` as `number | string`
     * exactly as it does a decimal — the trap CLAUDE.md calls out for
     * `ServerDecimal`, in a field nobody expects it in.
     *
     * `Number()` is right here and would be wrong three lines up: a sort order
     * is a display position, not an amount. Nothing rounds on it and nobody
     * pays it.
     */
    sortOrder: Number(row.sortOrder),
    isActive: row.isActive,
  }
}

function toSettings(row: NonNullable<SyncResponse['settings']>): MirroredSettings {
  return {
    currencyCode: row.currencyCode,
    timeZoneId: row.timeZoneId,
    taxMode: row.taxMode,
    cashRoundingIncrement: decimal(row.cashRoundingIncrement),
    businessDayStartOffset: row.businessDayStartOffset,
    addressLine: row.addressLine ?? null,
    taxNumber: row.taxNumber ?? null,
    receiptHeader: row.receiptHeader ?? null,
    receiptFooter: row.receiptFooter ?? null,
  }
}

/**
 * A `ServerDecimal` as the string the mirror stores.
 *
 * **Stored as text, never as a number.** `.NET`'s OpenAPI types every numeric as
 * `number | string`, and a price that went through a JS number on the way in
 * would arrive at the pricing engine already unable to represent a tenth — the
 * whole reason `lib/pricing` exists. `String(1.2)` is `"1.2"`, which is the
 * price somebody typed; the underlying double is not exactly that.
 */
function decimal(value: ServerDecimal): string {
  return typeof value === 'string' ? value : String(value)
}
