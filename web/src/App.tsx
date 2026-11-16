import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { ManageBookingPage, PublicBookingPage } from './pages/Booking.tsx'
import { UnsubscribePage } from './pages/Messaging.tsx'
import { useAuth } from './auth.tsx'
import { LoginPage } from './pages/Login.tsx'
import { ProductsPage } from './pages/Products.tsx'
import { ProductPage } from './pages/Product.tsx'
import { ApprovalsPage } from './pages/Approvals.tsx'
import { AdminPage } from './pages/Admin.tsx'
import { CalendarPage } from './pages/Content.tsx'
import { CrmPage } from './pages/Crm.tsx'
import { InboxPage, useWorkSummary } from './pages/Inbox.tsx'
import { AutomationPage } from './pages/Automation.tsx'
import { ConnectPage, PublishingPage } from './pages/Publishing.tsx'
import { AnalyticsPage } from './pages/Analytics.tsx'
import { BudgetPage } from './pages/Budget.tsx'
import { AccountPage } from './pages/Account.tsx'

export default function App() {
  const { me, loading, can, logout } = useAuth()
  const path = useLocation().pathname
  const summary = useWorkSummary(!!me)
  // Public booking pages work without signing in.
  if (path.startsWith('/book/')) return <Routes><Route path="/book/:key" element={<PublicBookingPage />} /></Routes>
  if (path.startsWith('/unsubscribe/')) return <Routes><Route path="/unsubscribe/:token" element={<UnsubscribePage />} /></Routes>
  if (path.startsWith('/booking/')) return <Routes><Route path="/booking/:token" element={<ManageBookingPage />} /></Routes>
  if (loading) return <p className="center muted">Loading…</p>
  if (!me) return <LoginPage />

  return (
    <div className="shell">
      <header className="topbar">
        <strong className="brand">TriSoftic Growth Engine</strong>
        <nav>
          {can('products.read') && <NavLink to="/products">Products</NavLink>}
          <NavLink to="/calendar">Calendar</NavLink>
          {can('crm.read') && <NavLink to="/crm">CRM</NavLink>}
          <NavLink to="/inbox">Inbox{!!summary.data?.unreadNotifications && <span className="badge warn"> {summary.data.unreadNotifications}</span>}{!!summary.data?.overdueTasks && <span className="badge warn"> {summary.data.overdueTasks} overdue</span>}</NavLink>
          {can('publishing.manage') && <NavLink to="/publishing">Publishing</NavLink>}
          {can('analytics.read') && <NavLink to="/analytics">Analytics</NavLink>}
          {can('budget.read') && <NavLink to="/budget">Budget</NavLink>}
          {can('automation.manage') && <NavLink to="/automation">Automation</NavLink>}
          <NavLink to="/approvals">Approvals</NavLink>
          {(can('audit.read') || can('jobs.manage') || can('governance.pause')) && <NavLink to="/admin">Admin</NavLink>}
        </nav>
        <span className="spacer" />
        <NavLink to="/account" className="muted">{me.displayName} · {me.roles.join(', ')}</NavLink>
        <button className="link" onClick={() => void logout()}>Sign out</button>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<Navigate to="/products" replace />} />
          <Route path="/products" element={<ProductsPage />} />
          <Route path="/products/:id/*" element={<ProductPage />} />
          <Route path="/calendar" element={<CalendarPage />} />
          <Route path="/crm/*" element={<CrmPage />} />
          <Route path="/inbox" element={<InboxPage />} />
          <Route path="/publishing" element={<PublishingPage />} />
          <Route path="/analytics" element={<AnalyticsPage />} />
          <Route path="/budget" element={<BudgetPage />} />
          <Route path="/account" element={<AccountPage />} />
          <Route path="/publishing/connect" element={<ConnectPage />} />
          <Route path="/automation" element={<AutomationPage />} />
          <Route path="/approvals"element={<ApprovalsPage />} />
          <Route path="/admin" element={<AdminPage />} />
          <Route path="*" element={<p>Page not found.</p>} />
        </Routes>
      </main>
    </div>
  )
}
