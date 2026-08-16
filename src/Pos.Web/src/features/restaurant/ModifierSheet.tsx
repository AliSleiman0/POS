import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { LoadingState } from '@/components/states'
import { parseServerDecimal } from '@/lib/money'
import { useModifierGroups } from './queries'

/** What the guest chose, ready to hang off the line. */
export interface ChosenModifiers {
  modifiers: { productId: string; quantity: number }[]
  note: string | null
}

/**
 * The questions an item asks.
 *
 * **The gating here is a courtesy and the server is the authority.** A client
 * that skipped a required group would send a steak to the grill with no
 * temperature on it, and the first anybody knew would be the chef shouting
 * across the pass — so `ModifierRules` refuses the line regardless of what this
 * sheet does. What the sheet adds is that the waiter finds out at the table
 * rather than after pressing send.
 *
 * **An in-page sheet, not a `confirm()`.** Invariant 10: a blocking dialog
 * stalls the event loop, and on a handheld in a busy room that is a service
 * waiting on a modal.
 */
export function ModifierSheet({
  productId,
  productName,
  onCancel,
  onConfirm,
}: {
  productId: string
  productName: string
  onCancel: () => void
  onConfirm: (chosen: ChosenModifiers) => void
}) {
  const groups = useModifierGroups(productId)
  const [chosen, setChosen] = useState<Record<string, string[]>>({})
  const [note, setNote] = useState('')

  if (groups.isPending) {
    return (
      <div className="rounded-lg border border-border p-3">
        <LoadingState label={`Reading the options for ${productName}…`} />
      </div>
    )
  }

  const asked = groups.data ?? []

  // Every group's minimum satisfied. Counted, not distinct-counted: "two extra
  // shots" is two selections of one option, and a shop that allows two means
  // two shots rather than two kinds of thing.
  const unmet = asked.filter(
    (group) => (chosen[group.id]?.length ?? 0) < Number(parseServerDecimal(group.minSelections)),
  )

  const toggle = (groupId: string, optionProductId: string, max: number | null) => {
    setChosen((current) => {
      const held = current[groupId] ?? []

      if (held.includes(optionProductId)) {
        return { ...current, [groupId]: held.filter((id) => id !== optionProductId) }
      }

      // A group of one replaces rather than refuses. "How would you like it
      // cooked?" answered twice means the waiter corrected themselves.
      const next = max === 1 ? [optionProductId] : [...held, optionProductId]

      return { ...current, [groupId]: next }
    })
  }

  return (
    <section
      aria-label={`Options for ${productName}`}
      data-testid="modifier-sheet"
      className="flex flex-col gap-3 rounded-lg border border-primary/30 bg-primary/5 p-3"
    >
      <h3 className="text-sm font-semibold text-foreground">{productName}</h3>

      {asked.map((group) => (
        <fieldset key={group.id} className="flex flex-col gap-1.5">
          <legend className="text-xs font-medium text-foreground">
            {group.name}
            {Number(parseServerDecimal(group.minSelections)) > 0 ? (
              <span className="ml-1 text-destructive" aria-label="required">
                required
              </span>
            ) : null}
          </legend>

          <div className="flex flex-wrap gap-2">
            {group.options.map((option) => {
              const picked = (chosen[group.id] ?? []).includes(option.productId)

              return (
                <Button
                  key={option.id}
                  type="button"
                  size="sm"
                  variant={picked ? 'default' : 'outline'}
                  aria-pressed={picked}
                  onClick={() =>
                    toggle(
                      group.id,
                      option.productId,
                      group.maxSelections === null
                        ? null
                        : Number(parseServerDecimal(group.maxSelections)),
                    )
                  }
                >
                  {option.name}
                </Button>
              )
            })}
          </div>
        </fieldset>
      ))}

      <label className="flex flex-col gap-1 text-xs text-muted-foreground">
        Note for the kitchen
        <Input
          value={note}
          onChange={(event) => setNote(event.target.value)}
          placeholder="Allergy, well done…"
        />
      </label>

      {unmet.length > 0 ? (
        <p role="status" className="text-xs text-destructive">
          Choose an option for {unmet.map((group) => group.name).join(', ')}.
        </p>
      ) : null}

      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button
          type="button"
          disabled={unmet.length > 0}
          data-testid="confirm-modifiers"
          onClick={() =>
            onConfirm({
              modifiers: Object.values(chosen)
                .flat()
                .map((id) => ({ productId: id, quantity: 1 })),
              note: note.trim() === '' ? null : note.trim(),
            })
          }
        >
          Add
        </Button>
      </div>
    </section>
  )
}
