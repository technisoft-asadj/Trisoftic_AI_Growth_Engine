import { useState, type FormEvent } from 'react'
import { useAuth } from '../auth.tsx'
import { ApiError } from '../api.ts'

export function LoginPage() {
  const { login } = useAuth()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true); setError(null)
    try { await login(email, password) } catch (err) {
      setError(err instanceof ApiError ? (err.status === 429 ? 'Too many attempts. Try again later.' : 'Invalid email or password.') : 'Could not reach the server.')
    } finally { setBusy(false) }
  }

  return (
    <form className="card login" onSubmit={(e) => void submit(e)}>
      <h1>Sign in</h1>
      <label>Email<input type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" required /></label>
      <label>Password<input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required /></label>
      {error && <p className="error" role="alert">{error}</p>}
      <button type="submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
    </form>
  )
}
