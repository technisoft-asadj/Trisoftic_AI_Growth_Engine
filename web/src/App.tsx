import { NavLink, Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth.tsx'
import { LoginPage } from './pages/Login.tsx'
import { ProductsPage } from './pages/Products.tsx'
import { ProductPage } from './pages/Product.tsx'
import { ApprovalsPage } from './pages/Approvals.tsx'
import { AdminPage } from './pages/Admin.tsx'
import { CalendarPage } from './pages/Content.tsx'

export default function App() {
  const { me, loading, can, logout } = useAuth()
  if (loading) return <p className="center muted">Loading…</p>
  if (!me) return <LoginPage />

  return (
    <div className="shell">
      <header className="topbar">
        <strong className="brand">TriSoftic Growth Engine</strong>
        <nav>
          {can('products.read') && <NavLink to="/products">Products</NavLink>}
          <NavLink to="/calendar">Calendar</NavLink>
          <NavLink to="/approvals">Approvals</NavLink>
          {(can('audit.read') || can('jobs.manage') || can('governance.pause')) && <NavLink to="/admin">Admin</NavLink>}
        </nav>
        <span className="spacer" />
        <span className="muted">{me.displayName} · {me.roles.join(', ')}</span>
        <button className="link" onClick={() => void logout()}>Sign out</button>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<Navigate to="/products" replace />} />
          <Route path="/products" element={<ProductsPage />} />
          <Route path="/products/:id/*" element={<ProductPage />} />
          <Route path="/calendar" element={<CalendarPage />} />
          <Route path="/approvals" element={<ApprovalsPage />} />
          <Route path="/admin" element={<AdminPage />} />
          <Route path="*" element={<p>Page not found.</p>} />
        </Routes>
      </main>
    </div>
  )
}
