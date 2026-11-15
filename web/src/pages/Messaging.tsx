import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api, ApiError } from '../api.ts'

interface Msg { id: string; to?: string; purpose: string; subject: string; status: string; reason?: string; queuedAt: string; sentAt?: string }
interface Status { providerConfigured: boolean; from?: string; marketingReady: boolean }

const badge = (s: string) => (s === 'Sent' ? 'badge ok' : s === 'Blocked' || s === 'Failed' ? 'badge warn' : 'badge')

export function MessagesTab() {
  const [status, setStatus] = useState('')
  const st = useQuery({ queryKey: ['msg-status'], queryFn: () => api.get<Status>('/api/v1/messaging/status') })
  const q = useQuery({ queryKey: ['messages', status], queryFn: () => api.get<Msg[]>(`/api/v1/messaging/messages${status ? `?status=${status}` : ''}`), refetchInterval: 30_000 })
  return (
    <>
      {st.data && !st.data.providerConfigured && <p className="error" role="alert">No email service is connected on this server, so queued emails will fail. Set Messaging:Smtp:Host and Messaging:From.</p>}
      {st.data?.providerConfigured && !st.data.marketingReady && <p className="muted">Marketing email is switched off until a public web address, link secret and a postal-address footer are configured (so every marketing email shows who sent it and how to stop it).</p>}
      <div className="row">
        <label>Show<select value={status} onChange={(e) => setStatus(e.target.value)}><option value="">All</option>{['Queued', 'Sent', 'Blocked', 'Failed'].map((s) => <option key={s}>{s}</option>)}</select></label>
      </div>
      <p className="muted">“Blocked” means the consent check said no at the moment of sending: no recorded opt-in, an opt-out, a bounce, or the address is on the do-not-contact list.</p>
      {q.data?.length === 0 && <p className="muted">No emails yet.</p>}
      {!!q.data?.length && (
        <table>
          <thead><tr><th>Queued</th><th>Type</th><th>Subject</th><th>Status</th></tr></thead>
          <tbody>{q.data.map((m) => (
            <tr key={m.id}><td>{new Date(m.queuedAt).toLocaleString()}</td><td>{m.purpose}</td><td>{m.subject}{m.to && <div className="muted">{m.to}</div>}</td>
              <td><span className={badge(m.status)}>{m.status}</span>{m.reason && m.status !== 'Sent' && <div className="muted">{m.reason}</div>}</td></tr>
          ))}</tbody>
        </table>
      )}
    </>
  )
}

/** Public page behind the unsubscribe link in marketing emails. No sign-in. */
export function UnsubscribePage() {
  const { token } = useParams()
  const info = useQuery({ queryKey: ['unsub', token], retry: false, queryFn: () => api.get<{ address: string }>(`/api/v1/public/unsubscribe/${token}`) })
  const go = useMutation({ mutationFn: () => api.post(`/api/v1/public/unsubscribe/${token}`) })
  return (
    <main style={{ maxWidth: 520, margin: '3rem auto' }}>
      <h1>Unsubscribe</h1>
      {info.isLoading && <p className="muted">Loading…</p>}
      {info.isError && <p className="error">This link is not valid.</p>}
      {info.data && !go.isSuccess && (
        <>
          <p>Stop marketing emails to <strong>{info.data.address}</strong>?</p>
          <p className="muted">You will still get messages about things you asked for, such as booking confirmations.</p>
          <button disabled={go.isPending} onClick={() => go.mutate()}>Yes, unsubscribe me</button>
          {go.error instanceof ApiError && <p className="error" role="alert">{go.error.message}</p>}
        </>
      )}
      {go.isSuccess && <p>Done. You will not receive marketing emails from us any more.</p>}
    </main>
  )
}
