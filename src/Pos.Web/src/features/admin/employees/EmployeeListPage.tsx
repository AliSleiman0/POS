import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { ConfirmButton } from '@/components/ConfirmButton'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { useAuth } from '@/auth/authContext'
import { EmployeeFormDialog } from './EmployeeFormDialog'
import { ResetPinDialog } from './ResetPinDialog'
import { useDeactivateEmployee, useEmployees, type EmployeeSummary } from './queries'

/**
 * The staff list. Owner-only, and the screen that means a shop can hire
 * somebody without contacting us.
 */
export function EmployeeListPage() {
  const toast = useToast()
  const { user } = useAuth()

  const [activeOnly, setActiveOnly] = useState(false)
  const [editing, setEditing] = useState<EmployeeSummary | null>(null)
  const [adding, setAdding] = useState(false)
  const [pinFor, setPinFor] = useState<EmployeeSummary | null>(null)

  const employees = useEmployees(activeOnly)
  const deactivate = useDeactivateEmployee()

  const rows = employees.data ?? []

  return (
    <div className="mx-auto flex max-w-4xl flex-col gap-6 p-6">
      <header className="flex items-center justify-between gap-4">
        <h1 className="text-xl font-semibold text-foreground">People</h1>
        <Button
          onClick={() => {
            setAdding(true)
          }}
        >
          Add somebody
        </Button>
      </header>

      <div className="flex flex-wrap items-end gap-3">
        <label className="flex h-9 items-center gap-2 text-sm text-muted-foreground">
          <input
            type="checkbox"
            className="size-4"
            checked={activeOnly}
            onChange={(event) => {
              setActiveOnly(event.target.checked)
            }}
          />
          Current staff only
        </label>
      </div>

      {employees.isPending ? (
        <LoadingState label="Loading the staff list…" />
      ) : employees.isError ? (
        <ErrorState
          error={employees.error}
          onRetry={() => {
            void employees.refetch()
          }}
          title="Could not load the staff list."
        />
      ) : rows.length === 0 ? (
        // The filtered case is the only reachable empty one: a signed-in owner is
        // always in the unfiltered list, so "no staff at all" cannot happen here.
        <EmptyState
          title="Nobody matched."
          description="Clear the filter to see everybody, including people who have left."
        />
      ) : (
        <div className="overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-sm">
            <thead className="bg-muted text-left text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2 font-medium">Name</th>
                <th className="px-3 py-2 font-medium">Email</th>
                <th className="px-3 py-2 font-medium">Role</th>
                <th className="px-3 py-2 font-medium">Till PIN</th>
                <th className="px-3 py-2 font-medium">Status</th>
                <th className="px-3 py-2" />
              </tr>
            </thead>
            <tbody>
              {rows.map((employee) => {
                const isSelf = employee.id === user?.id

                return (
                  <tr
                    key={employee.id}
                    className="border-t border-border"
                    data-testid="employee-row"
                  >
                    <td className="px-3 py-2 font-medium text-foreground">
                      {employee.displayName}
                      {isSelf ? (
                        <span className="ml-2 text-xs font-normal text-muted-foreground">you</span>
                      ) : null}
                    </td>
                    <td className="px-3 py-2 text-muted-foreground">{employee.email}</td>
                    <td className="px-3 py-2">{employee.role ?? '—'}</td>
                    <td className="px-3 py-2 text-muted-foreground">
                      {employee.hasPin ? 'Set' : 'None'}
                    </td>
                    <td className="px-3 py-2">
                      {employee.isActive ? (
                        <span className="text-muted-foreground">Active</span>
                      ) : (
                        <span className="font-medium text-destructive">Deactivated</span>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex justify-end gap-2">
                        <Button
                          variant="outline"
                          size="sm"
                          onClick={() => {
                            setEditing(employee)
                          }}
                        >
                          Edit
                        </Button>

                        <Button
                          variant="outline"
                          size="sm"
                          onClick={() => {
                            setPinFor(employee)
                          }}
                        >
                          {employee.hasPin ? 'Reset PIN' : 'Set PIN'}
                        </Button>

                        {/*
                          Hidden for yourself, because the server refuses it and a
                          button whose only outcome is an error is a worse
                          explanation than its absence. The server is still the
                          gate — this is convenience, per invariant 7.
                        */}
                        {employee.isActive && !isSelf ? (
                          <ConfirmButton
                            confirmLabel="Really deactivate?"
                            onConfirm={() => {
                              deactivate.mutate(employee.id, {
                                onSuccess: () => {
                                  toast.show(`${employee.displayName} can no longer sign in.`)
                                },
                                onError: (caught) => {
                                  toast.showError(caught, 'Could not deactivate this person.')
                                },
                              })
                            }}
                          >
                            Deactivate
                          </ConfirmButton>
                        ) : null}
                      </div>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {adding ? (
        <EmployeeFormDialog
          employee={null}
          onClose={() => {
            setAdding(false)
          }}
        />
      ) : null}

      {editing !== null ? (
        <EmployeeFormDialog
          employee={editing}
          onClose={() => {
            setEditing(null)
          }}
        />
      ) : null}

      {pinFor !== null ? (
        <ResetPinDialog
          employee={pinFor}
          onClose={() => {
            setPinFor(null)
          }}
        />
      ) : null}
    </div>
  )
}
