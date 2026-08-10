import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input, Select } from '@/components/ui/input'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useEmployees } from '@/features/admin/employees/queries'
import {
  ACTIONS,
  EMPTY_FILTERS,
  useAuditEntries,
  type AuditEntry,
  type AuditFilters,
} from './queries'

/**
 * What has been done that moves money.
 *
 * Newest first and paginated, which is also what keeps it honest as a shop's history
 * grows: whatever just happened is on the first page, however many years are behind it.
 */
export function AuditListPage() {
  const [filters, setFilters] = useState<AuditFilters>(EMPTY_FILTERS)

  const entries = useAuditEntries(filters)
  const staff = useEmployees(false)

  const rows = entries.data?.pages.flatMap((page) => page.items) ?? []
  const filtered = Object.values(filters).some((value) => value !== '')

  const set = (patch: Partial<AuditFilters>) => {
    setFilters((current) => ({ ...current, ...patch }))
  }

  return (
    <div className="mx-auto flex max-w-5xl flex-col gap-6 p-6">
      <header>
        <h1 className="text-xl font-semibold text-foreground">Activity</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Discounts, overrides, voids, refunds, stock corrections and who has access. Entries cannot
          be edited or removed &mdash; not by anybody, including us.
        </p>
      </header>

      <div className="flex flex-wrap items-end gap-3 rounded-xl border border-border p-3">
        <Field label="Action" className="min-w-48">
          {(fieldProps) => (
            <Select
              {...fieldProps}
              value={filters.action}
              onChange={(event) => {
                set({ action: event.target.value })
              }}
            >
              <option value="">Anything</option>
              {ACTIONS.map((action) => (
                <option key={action.value} value={action.value}>
                  {action.label}
                </option>
              ))}
            </Select>
          )}
        </Field>

        <Field label="Who" className="min-w-48">
          {(fieldProps) => (
            <Select
              {...fieldProps}
              value={filters.actorId}
              onChange={(event) => {
                set({ actorId: event.target.value })
              }}
            >
              <option value="">Anybody</option>
              {(staff.data ?? []).map((employee) => (
                <option key={employee.id} value={employee.id}>
                  {employee.displayName}
                </option>
              ))}
            </Select>
          )}
        </Field>

        <Field label="From">
          {(fieldProps) => (
            <Input
              {...fieldProps}
              type="date"
              value={filters.from}
              onChange={(event) => {
                set({ from: event.target.value })
              }}
            />
          )}
        </Field>

        <Field label="To">
          {(fieldProps) => (
            <Input
              {...fieldProps}
              type="date"
              value={filters.to}
              onChange={(event) => {
                set({ to: event.target.value })
              }}
            />
          )}
        </Field>

        {filtered ? (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              setFilters(EMPTY_FILTERS)
            }}
          >
            Clear
          </Button>
        ) : null}
      </div>

      {entries.isPending ? (
        <LoadingState label="Loading the activity log…" />
      ) : entries.isError ? (
        <ErrorState
          error={entries.error}
          onRetry={() => {
            void entries.refetch()
          }}
          title="Could not load the activity log."
        />
      ) : rows.length === 0 ? (
        filtered ? (
          <EmptyState
            title="Nothing matched."
            description="Try a wider date range, or clear the filters."
          />
        ) : (
          // Genuinely empty is the normal state for a new shop, and it is good news
          // rather than a broken screen.
          <EmptyState
            title="Nothing recorded yet."
            description="Discounts, voids, refunds and stock corrections will appear here as they happen."
          />
        )
      ) : (
        <>
          <ul className="flex flex-col gap-2">
            {rows.map((entry) => (
              <EntryRow key={entry.id} entry={entry} />
            ))}
          </ul>

          {entries.hasNextPage ? (
            <Button
              variant="outline"
              className="self-center"
              disabled={entries.isFetchingNextPage}
              onClick={() => {
                void entries.fetchNextPage()
              }}
            >
              {entries.isFetchingNextPage ? 'Loading…' : 'Load more'}
            </Button>
          ) : null}
        </>
      )}
    </div>
  )
}

function EntryRow({ entry }: { entry: AuditEntry }) {
  const label = ACTIONS.find((a) => a.value === entry.action)?.label ?? entry.action

  return (
    <li className="rounded-lg border border-border bg-card p-3" data-testid="audit-row">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <p className="text-sm font-medium text-card-foreground">
          {label}
          <span className="ml-2 font-normal text-muted-foreground">
            by {entry.actorName}
            {entry.registerName === null ? '' : ` at ${entry.registerName}`}
          </span>
        </p>
        <p className="text-xs text-muted-foreground">{formatStamp(entry.occurredAt)}</p>
      </div>

      <Change before={entry.before} after={entry.after} />
    </li>
  )
}

/**
 * The before/after payload as a two-column table.
 *
 * Both sides are flat string maps from the server, which is the whole reason they are
 * `jsonb` objects on the wire rather than a JSON string: this renders directly, with no
 * second parse and no chance of showing a wall of escaped quotes.
 */
function Change({
  before,
  after,
}: {
  before: Record<string, string | null> | null
  after: Record<string, string | null> | null
}) {
  const keys = [...new Set([...Object.keys(before ?? {}), ...Object.keys(after ?? {})])]

  if (keys.length === 0) {
    return null
  }

  return (
    <dl className="mt-2 grid grid-cols-[auto_1fr_1fr] gap-x-4 gap-y-1 text-xs">
      {keys.map((key) => (
        <div key={key} className="contents">
          <dt className="text-muted-foreground">{key}</dt>
          <dd className="text-muted-foreground line-through decoration-muted-foreground/50">
            {before?.[key] ?? ''}
          </dd>
          <dd className="font-medium text-card-foreground">{after?.[key] ?? ''}</dd>
        </div>
      ))}
    </dl>
  )
}

/** UTC on the wire; the reader's own zone on a back-office screen. */
function formatStamp(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value))
}
