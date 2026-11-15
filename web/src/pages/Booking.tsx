import { useMemo, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api, ApiError } from '../api.ts'

// Public pages: no sign-in, no app chrome. They only talk to /api/v1/public/booking.
interface PageInfo { name: string; description?: string; durationMinutes: number; timeZone: string }
interface Slot { start: string; end: string }
interface Booked { bookingId: string; start: string; end: string; manageToken?: string; manageUrl?: string }
interface Managed { appointment: string; start: string; end: string; status: string; timeZone: string; publicKey: string; attendeeName: string }

const fmtDay = (d: Date) => d.toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long' })
const fmtTime = (s: string) => new Date(s).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
const fmtFull = (s: string) => new Date(s).toLocaleString(undefined, { dateStyle: 'full', timeStyle: 'short' })
const msg = (e: unknown) => (e instanceof ApiError ? (e.status === 429 ? 'Too many attempts; please wait a minute.' : e.message) : e ? 'Something went wrong' : null)
const myZone = Intl.DateTimeFormat().resolvedOptions().timeZone

function SlotPicker({ slots, value, onPick }: { slots: Slot[]; value: string; onPick: (s: string) => void }) {
  const days = useMemo(() => {
    const m = new Map<string, Slot[]>()
    for (const s of slots) { const k = new Date(s.start).toDateString(); m.set(k, [...(m.get(k) ?? []), s]) }
    return [...m.entries()].slice(0, 14)
  }, [slots])
  if (days.length === 0) return <p className="muted">There are no free times in the next two weeks. Please check back later.</p>
  return (
    <>
      {days.map(([day, list]) => (
        <div key={day}>
          <h3>{fmtDay(new Date(list[0].start))}</h3>
          <div className="row">
            {list.map((s) => <button key={s.start} className={s.start === value ? '' : 'secondary'} onClick={() => onPick(s.start)}>{fmtTime(s.start)}</button>)}
          </div>
        </div>
      ))}
    </>
  )
}

export function PublicBookingPage() {
  const { key } = useParams(); const [sp] = useSearchParams()
  const ref = sp.get('ref')
  const info = useQuery({ queryKey: ['pub-info', key], queryFn: () => api.get<PageInfo>(`/api/v1/public/booking/${key}`), retry: false })
  const slots = useQuery({ queryKey: ['pub-slots', key], queryFn: () => api.get<Slot[]>(`/api/v1/public/booking/${key}/slots`), enabled: info.isSuccess })
  const [start, setStart] = useState(''); const [name, setName] = useState(''); const [email, setEmail] = useState(''); const [phone, setPhone] = useState(''); const [notes, setNotes] = useState('')
  const [idem] = useState(() => crypto.randomUUID())
  const book = useMutation({
    mutationFn: () => api.post<Booked>(`/api/v1/public/booking/${key}/book`, { start, name, email, phone: phone || null, notes: notes || null, ref, idempotencyKey: idem }),
    onError: () => void slots.refetch(),   // the slot may have just been taken; show fresh times
  })

  if (info.isLoading) return <Shell><p className="muted">Loading…</p></Shell>
  if (!info.data) return <Shell><h1>Page not found</h1><p>This booking link is not valid or has been switched off.</p></Shell>
  if (book.data) {
    return (
      <Shell>
        <h1>You’re booked</h1>
        <p><strong>{info.data.name}</strong><br />{fmtFull(book.data.start)} ({myZone})</p>
        {book.data.manageUrl && <p>To change or cancel, use <a href={book.data.manageUrl}>this private link</a>. Keep it safe: anyone with it can change the booking.</p>}
      </Shell>
    )
  }
  return (
    <Shell>
      <h1>{info.data.name}</h1>
      <p className="muted">{info.data.durationMinutes} minutes{info.data.description && ` · ${info.data.description}`}. Times are shown in your time zone ({myZone}).</p>
      <SlotPicker slots={slots.data ?? []} value={start} onPick={setStart} />
      {start && (
        <div className="card">
          <h2>{fmtFull(start)}</h2>
          <label>Your name<input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} /></label>
          <label>Email<input type="email" value={email} onChange={(e) => setEmail(e.target.value)} maxLength={320} /></label>
          <label>Phone (optional)<input value={phone} onChange={(e) => setPhone(e.target.value)} maxLength={50} /></label>
          <label>Anything we should know? (optional)<textarea value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={2000} rows={3} /></label>
          <button disabled={!name.trim() || !email.includes('@') || book.isPending} onClick={() => book.mutate()}>Confirm booking</button>
          {msg(book.error) && <p className="error" role="alert">{msg(book.error)}</p>}
        </div>
      )}
    </Shell>
  )
}

export function ManageBookingPage() {
  const { token } = useParams()
  const q = useQuery({ queryKey: ['pub-manage', token], queryFn: () => api.get<Managed>(`/api/v1/public/booking/manage/${token}`), retry: false })
  const slots = useQuery({ queryKey: ['pub-manage-slots', q.data?.publicKey], enabled: q.data?.status === 'Confirmed', queryFn: () => api.get<Slot[]>(`/api/v1/public/booking/${q.data!.publicKey}/slots`) })
  const [pick, setPick] = useState(''); const [reason, setReason] = useState(''); const [rescheduling, setRescheduling] = useState(false)
  const [moved, setMoved] = useState<Booked | null>(null)
  const cancel = useMutation({ mutationFn: () => api.post(`/api/v1/public/booking/manage/${token}/cancel`, { reason: reason || null }), onSuccess: () => void q.refetch() })
  const move = useMutation({
    mutationFn: () => api.post<Booked>(`/api/v1/public/booking/manage/${token}/reschedule`, { start: pick }),
    onSuccess: (b) => { setMoved(b); void q.refetch() },
    onError: () => void slots.refetch(),
  })

  if (q.isLoading) return <Shell><p className="muted">Loading…</p></Shell>
  if (!q.data) return <Shell><h1>Link not valid</h1><p>This booking link is not valid.</p></Shell>
  const b = q.data
  if (moved) return <Shell><h1>Moved</h1><p>Your new time: <strong>{fmtFull(moved.start)}</strong>. A new private link: <a href={moved.manageUrl}>{moved.manageUrl}</a></p></Shell>
  return (
    <Shell>
      <h1>{b.appointment}</h1>
      <p><strong>{fmtFull(b.start)}</strong> ({myZone}) · <span className={b.status === 'Confirmed' ? 'badge ok' : 'badge warn'}>{b.status}</span></p>
      {b.status === 'Confirmed' && (
        <>
          {!rescheduling ? (
            <div className="row"><button className="secondary" onClick={() => setRescheduling(true)}>Choose a different time</button></div>
          ) : (
            <div className="card">
              <SlotPicker slots={slots.data ?? []} value={pick} onPick={setPick} />
              <button disabled={!pick || move.isPending} onClick={() => move.mutate()}>Move my booking</button>
              {msg(move.error) && <p className="error" role="alert">{msg(move.error)}</p>}
            </div>
          )}
          <div className="card">
            <label>Cancel this booking (reason optional)<input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></label>
            <button className="danger" disabled={cancel.isPending} onClick={() => cancel.mutate()}>Cancel booking</button>
            {msg(cancel.error) && <p className="error" role="alert">{msg(cancel.error)}</p>}
          </div>
        </>
      )}
    </Shell>
  )
}

function Shell({ children }: { children: React.ReactNode }) {
  return <main style={{ maxWidth: 640, margin: '2rem auto' }}>{children}</main>
}
