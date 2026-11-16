import { useState, type FormEvent } from 'react'
import { useMutation } from '@tanstack/react-query'
import { api, ApiError } from '../api.ts'
import { useAuth } from '../auth.tsx'

export function AccountPage() {
  const { me } = useAuth()
  const [current, setCurrent] = useState(''); const [next, setNext] = useState(''); const [again, setAgain] = useState('')
  const change = useMutation({
    mutationFn: () => api.post('/api/v1/auth/change-password', { currentPassword: current, newPassword: next }),
    onSuccess: () => { setCurrent(''); setNext(''); setAgain('') },
  })
  const mismatch = again.length > 0 && next !== again
  const e = change.error instanceof ApiError ? change.error : null
  const errors = e?.violations ?? []
  function submit(ev: FormEvent) { ev.preventDefault(); if (!mismatch) change.mutate() }
  return (
    <>
      <h1>Account</h1>
      <p className="muted">{me?.displayName} · {me?.email}</p>
      <form className="card" onSubmit={submit}>
        <h2>Change password</h2>
        <label>Current password<input type="password" value={current} onChange={(x) => setCurrent(x.target.value)} autoComplete="current-password" required /></label>
        <label>New password<input type="password" value={next} onChange={(x) => setNext(x.target.value)} autoComplete="new-password" required minLength={12} /></label>
        <label>New password again<input type="password" value={again} onChange={(x) => setAgain(x.target.value)} autoComplete="new-password" required /></label>
        {mismatch && <p className="error">The two new passwords do not match.</p>}
        {e && <div className="error" role="alert">{e.message}{errors.length > 0 && <ul>{errors.map((m) => <li key={m}>{m}</li>)}</ul>}</div>}
        {change.isSuccess && <p className="badge ok" role="status">Password changed. Other devices have been signed out.</p>}
        <button type="submit" disabled={change.isPending || mismatch || !current || !next}>Change password</button>
      </form>
    </>
  )
}
