import { createBrowserRouter, Navigate } from 'react-router'
import { AppLayout } from './AppLayout'
import { ErrorBoundary } from './ErrorBoundary'
import { OverviewPage } from './OverviewPage'
import { RequireAuth, RequirePolicy } from '@/auth/guards'
import { DeviceEnrollmentPage } from '@/auth/DeviceEnrollmentPage'
import { LoginPage } from '@/auth/LoginPage'
import { PinSwapPage } from '@/auth/PinSwapPage'
import { CategoriesPage } from '@/features/catalog/CategoriesPage'
import { ProductDetailPage } from '@/features/catalog/ProductDetailPage'
import { ProductListPage } from '@/features/catalog/ProductListPage'
import { TaxClassesPage } from '@/features/catalog/TaxClassesPage'
import { StockPage } from '@/features/catalog/StockPage'
import { RegisterPage } from '@/features/register/RegisterPage'
import { DailyReportPage } from '@/features/reports/DailyReportPage'
import { ShiftReportPage } from '@/features/reports/ShiftReportPage'

/**
 * Routes.
 *
 * `RequireAuth` is a layout route rather than a wrapper per page, so the
 * authenticated tree mounts once. That matters for more than tidiness: it is
 * what lets an expired session render a prompt *over* the current screen
 * without unmounting it. See `ReauthOverlay`.
 */
export const router = createBrowserRouter([
  {
    path: '/login',
    element: <LoginPage />,
    // A crash on the login page must not be a blank screen — there would be no
    // way back from it.
    errorElement: <ErrorBoundary>{null}</ErrorBoundary>,
  },
  {
    // The till's idle state. Outside the guard: nobody is signed in yet, which
    // is the entire point of the screen.
    path: '/pin',
    element: <PinSwapPage />,
  },
  {
    element: <RequireAuth />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <OverviewPage /> },

          {
            // The till. Inside `AppLayout` so the cart, which lives above the
            // outlet, is not remounted when a cashier looks something up.
            path: 'register',
            element: (
              <RequirePolicy policy="CanSell">
                <RegisterPage />
              </RequirePolicy>
            ),
          },

          {
            path: 'catalog',
            element: <RequirePolicy policy="CanManageCatalog" />,
            children: [
              { index: true, element: <ProductListPage /> },
              { path: 'products/:productId', element: <ProductDetailPage /> },
              { path: 'categories', element: <CategoriesPage /> },
              { path: 'tax-classes', element: <TaxClassesPage /> },
            ],
          },

          {
            // CanCloseShift, matching the API. The expected cash in a drawer is
            // not a cashier's business — knowing it is knowing what a till would
            // tolerate.
            path: 'reports',
            element: <RequirePolicy policy="CanCloseShift" />,
            children: [
              { index: true, element: <Navigate to="/reports/daily" replace /> },
              { path: 'daily', element: <DailyReportPage /> },
              { path: 'shift/:shiftId', element: <ShiftReportPage /> },
            ],
          },

          {
            path: 'stock',
            element: (
              <RequirePolicy policy="CanManageCatalog">
                <StockPage />
              </RequirePolicy>
            ),
          },

          {
            path: 'settings/device',
            element: (
              <RequirePolicy policy="CanManageEmployees">
                <DeviceEnrollmentPage />
              </RequirePolicy>
            ),
          },
        ],
      },
    ],
  },
  { path: '*', element: <Navigate to="/" replace /> },
])
