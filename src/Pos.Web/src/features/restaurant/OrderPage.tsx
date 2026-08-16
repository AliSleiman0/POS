import { useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { ErrorType, isErrorType, isProblemError } from '@/api/problem'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { parseServerDecimal } from '@/lib/money'
import { useProducts } from '@/features/catalog/queries'
import { ModifierSheet, type ChosenModifiers } from './ModifierSheet'
import { useOperationKey } from './idempotency'
import { VoidLineDialog } from './VoidLineDialog'
import { useAddLines, useFire, useOrder, type LineToAdd, type OrderLine } from './queries'

/**
 * One table's order.
 *
 * **There is no total on this screen, deliberately.** `OrderLine` stores inputs
 * — description, unit price, tax rate, discount — and not amounts, because the
 * money is computed by the pricing engine when a bill is quoted or settled. A
 * running total rendered here would be a second set of numbers to keep in step
 * through every edit, void and re-split, and the first time it drifted the
 * screen would show something the sale would not charge. The bill is one tap
 * away and it is priced by the server.
 *
 * Lines group by **course**, which is how a dining room works and how firing is
 * addressed. A modifier is nested under the line it modifies rather than listed
 * beside it, because "remove the burger" has to visibly take its extras with it.
 */
export function OrderPage() {
  const { orderId = null } = useParams<{ orderId: string }>()
  const navigate = useNavigate()

  const order = useOrder(orderId)

  if (order.isPending) {
    return <LoadingState label="Reading the order…" />
  }

  if (order.isError) {
    return (
      <ErrorState title="Could not read that order." error={order.error} onRetry={order.refetch} />
    )
  }

  const lines = order.data.lines.filter((line) => line.status !== 'Voided')

  // Course is a ServerDecimal (`number | string`) like every numeric field on
  // the wire — CLAUDE.md is explicit that a guard accepting only one of them
  // compiles, passes review and rejects every value at runtime. Narrowed once,
  // here, so the rest of this screen works in numbers.
  const courseOf = (line: OrderLine) => Number(parseServerDecimal(line.course))
  const courses = [...new Set(lines.map(courseOf))].sort((a, b) => a - b)

  return (
    <div className="flex flex-col gap-4 p-4">
      <header className="flex flex-wrap items-baseline justify-between gap-3">
        <div className="flex flex-col">
          <h1 className="text-lg font-semibold text-foreground">
            {order.data.tableName === null
              ? (order.data.tabName ?? 'Takeaway')
              : `Table ${order.data.tableName}`}
          </h1>
          <p className="text-xs text-muted-foreground">
            Order #{order.data.orderNumber}
            {order.data.coverCount === null ? '' : ` · ${order.data.coverCount} covers`}
          </p>
        </div>

        <div className="flex gap-2">
          <Button type="button" variant="outline" onClick={() => void navigate('/register')}>
            Back to the floor
          </Button>
          <Button
            type="button"
            disabled={lines.length === 0}
            onClick={() => void navigate(`/restaurant/orders/${orderId!}/bill`)}
          >
            Bill
          </Button>
        </div>
      </header>

      {courses.length === 0 ? (
        <EmptyState title="Nothing on this order yet." description="Add the first round below." />
      ) : (
        courses.map((course) => (
          <Course
            key={course}
            orderId={orderId!}
            course={course}
            lines={lines.filter(
              (line) => courseOf(line) === course && line.parentOrderLineId === null,
            )}
            all={lines}
          />
        ))
      )}

      <AddRound orderId={orderId!} />
    </div>
  )
}

/** One round, and the button that sends it. */
function Course({
  orderId,
  course,
  lines,
  all,
}: {
  orderId: string
  course: number
  lines: readonly OrderLine[]
  all: readonly OrderLine[]
}) {
  const toast = useToast()
  const key = useOperationKey()
  const fire = useFire(orderId, key.key)

  const pending = lines.filter((line) => line.status === 'Pending')

  const send = async () => {
    try {
      const tickets = await fire.mutateAsync(course)

      // An empty list is a success, not a failure: the round is already away,
      // which is what a second tap looks like. Saying "sent" would be a lie and
      // showing an error would be worse.
      toast.show(
        tickets.length === 0
          ? 'That round is already with the kitchen.'
          : `Away to ${tickets.map((ticket) => ticket.stationName).join(' and ')}.`,
        { tone: 'success' },
      )

      key.renew()
    } catch (caught) {
      if (isErrorType(caught, ErrorType.productNotRouted)) {
        // The menu routes something nowhere. Naming it is the whole value of the
        // refusal — a manager fixes it in thirty seconds.
        toast.show('Something here has no station to be cooked at.', {
          tone: 'error',
          detail: isProblemError(caught) ? caught.problem.detail : undefined,
        })
        return
      }

      toast.showError(caught, 'Could not send that round.')
    }
  }

  return (
    <section
      aria-label={`Course ${course}`}
      className="flex flex-col gap-2 rounded-xl border border-border p-3"
    >
      <div className="flex items-center justify-between gap-3">
        <h2 className="text-sm font-semibold text-foreground">Course {course}</h2>

        <Button
          type="button"
          variant={pending.length === 0 ? 'ghost' : 'default'}
          size="sm"
          disabled={pending.length === 0 || fire.isPending}
          onClick={() => void send()}
          data-testid={`fire-course-${course}`}
        >
          {pending.length === 0 ? 'Away' : `Fire ${pending.length}`}
        </Button>
      </div>

      <ul className="flex flex-col gap-1">
        {lines.map((line) => (
          <Line
            key={line.id}
            orderId={orderId}
            line={line}
            modifiers={all.filter((candidate) => candidate.parentOrderLineId === line.id)}
          />
        ))}
      </ul>
    </section>
  )
}

/** One item, with its modifiers underneath it. */
function Line({
  orderId,
  line,
  modifiers,
}: {
  orderId: string
  line: OrderLine
  modifiers: readonly OrderLine[]
}) {
  const [voiding, setVoiding] = useState(false)

  return (
    <li className="flex flex-col gap-0.5 border-b border-border/50 py-1.5 last:border-0">
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-sm text-foreground">
          <span className="tabular-nums text-muted-foreground">{line.quantity}×</span>{' '}
          {line.description}
          {line.seatNumber === null ? null : (
            <span className="ml-2 text-xs text-muted-foreground">seat {line.seatNumber}</span>
          )}
        </span>

        <div className="flex items-center gap-2">
          {line.status === 'Fired' ? (
            <span className="text-xs font-medium text-muted-foreground">away</span>
          ) : null}

          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => setVoiding(true)}
            aria-label={`Remove ${line.description}`}
          >
            Remove
          </Button>
        </div>
      </div>

      {modifiers.map((modifier) => (
        <span key={modifier.id} className="pl-4 text-xs text-muted-foreground">
          {modifier.description}
        </span>
      ))}

      {line.note === null ? null : (
        <span className="pl-4 text-xs font-medium text-foreground">{line.note}</span>
      )}

      {voiding ? (
        <VoidLineDialog orderId={orderId} line={line} onClose={() => setVoiding(false)} />
      ) : null}
    </li>
  )
}

/** Adding the next round. */
function AddRound({ orderId }: { orderId: string }) {
  const toast = useToast()
  const key = useOperationKey()
  const add = useAddLines(orderId, key.key)

  const [search, setSearch] = useState('')
  const [course, setCourse] = useState(1)
  const [seat, setSeat] = useState('')
  const [choosing, setChoosing] = useState<{ id: string; name: string } | null>(null)

  // Modifiers never appear here: `isModifier` keeps them out of the product
  // list server-side, because nobody orders "extra cheese" on its own.
  const products = useProducts({ q: search, categoryId: '', activeOnly: true })

  const items = useMemo(
    () => products.data?.pages.flatMap((page) => page.items) ?? [],
    [products.data],
  )

  const put = async (line: LineToAdd) => {
    try {
      await add.mutateAsync([line])
      key.renew()
      setChoosing(null)
    } catch (caught) {
      toast.showError(caught, 'Could not add that.')
    }
  }

  return (
    <section
      aria-label="Add items"
      className="flex flex-col gap-3 rounded-xl border border-border p-3"
    >
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex flex-1 flex-col gap-1 text-xs text-muted-foreground">
          Search the menu
          <Input
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder="Burger, wine…"
          />
        </label>

        <label className="flex w-24 flex-col gap-1 text-xs text-muted-foreground">
          Course
          <Input
            type="number"
            min={1}
            value={course}
            onChange={(event) => setCourse(Math.max(1, Number(event.target.value) || 1))}
          />
        </label>

        <label className="flex w-24 flex-col gap-1 text-xs text-muted-foreground">
          Seat
          <Input
            type="number"
            min={1}
            value={seat}
            placeholder="—"
            onChange={(event) => setSeat(event.target.value)}
          />
        </label>
      </div>

      {products.isPending ? (
        <LoadingState label="Reading the menu…" />
      ) : (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(8rem,1fr))] gap-2">
          {items.map((product) => (
            <Button
              key={product.id}
              type="button"
              variant="outline"
              className="h-16 flex-col items-start justify-center gap-0.5 p-2 text-left"
              disabled={add.isPending}
              onClick={() => setChoosing({ id: product.id, name: product.name })}
            >
              <span className="line-clamp-2 text-xs font-medium">{product.name}</span>
            </Button>
          ))}
        </div>
      )}

      {choosing === null ? null : (
        <ModifierSheet
          productId={choosing.id}
          productName={choosing.name}
          onCancel={() => setChoosing(null)}
          onConfirm={(chosen: ChosenModifiers) =>
            void put({
              productId: choosing.id,
              quantity: 1,
              course,
              seatNumber: seat === '' ? null : Number(seat),
              note: chosen.note,
              modifiers: chosen.modifiers,
            })
          }
        />
      )}
    </section>
  )
}
