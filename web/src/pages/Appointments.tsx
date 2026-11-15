import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type Product } from '../api.ts'

interface TypeRow { id: string; name: string; durationMinutes: number; timeZone: string; staffUserIds: string[]; publicKey: string; enabled: boolean; bufferBeforeMinutes: number; bufferAfterMinutes: number }
interface Avail { rules: { dayOfWeek: number; start: string; end: string }[]; closedDates: string[] }
interface BookingRow { id: string; start: string; end: string; status: string; attendeeName: string; attendeeEmail: string; leadId?: string; staffUserId: string }
interface Person { id: string; displayName: string }

const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const err = (e: unknown) => (e instanceof ApiError ? e.message : e ? 'Something went wrong' : null)

export function AppointmentsTab() {
  const products = useQuery({ queryKey: ['products'], queryFn: () => api.get<Product[]>('/api/v1/products') })
  const [chosen, setChosen] = useState('')
  const pid = chosen || products.data?.find((p) => !p.isArchived)?.id || ''
  return (
    <>
      <div className="row">
        <label>Product
          <select value={pid} onChange={(e) => setChosen(e.target.value)}>{products.data?.filter((p) => !p.isArchived).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select>
        </label>
      </div>
      {pid ? <><Types productId={pid} /><Upcoming productId={pid} /></> : <p className="muted">Create a product first.</p>}
    </>
  )
}

function Types({ productId }: { productId: string }) {
  const qc = useQueryClient()
  const types = useQuery({ queryKey: ['appt-types', productId], queryFn: () => api.get<TypeRow[]>(`/api/v1/scheduling/products/${productId}/appointment-types`) })
  const people = useQuery({ queryKey: ['directory'], queryFn: () => api.get<Person[]>('/api/v1/directory/users') })
  const [name, setName] = useState('Product demo'); const [dur, setDur] = useState('30'); const [zone, setZone] = useState(Intl.DateTimeFormat().resolvedOptions().timeZone)
  const [before, setBefore] = useState('0'); const [after, setAfter] = useState('0'); const [staff, setStaff] = useState<string[]>([])
  const [editing, setEditing] = useState('')
  const create = useMutation({
    mutationFn: () => api.post(`/api/v1/scheduling/products/${productId}/appointment-types`, { name, durationMinutes: Number(dur), timeZone: zone, bufferBeforeMinutes: Number(before), bufferAfterMinutes: Number(after), staffUserIds: staff }),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['appt-types', productId] }) },
  })
  const toggle = useMutation({
    mutationFn: (t: TypeRow) => api.put(`/api/v1/scheduling/appointment-types/${t.id}`, { name: t.name, durationMinutes: t.durationMinutes, timeZone: t.timeZone, staffUserIds: t.staffUserIds, bufferBeforeMinutes: t.bufferBeforeMinutes, bufferAfterMinutes: t.bufferAfterMinutes, enabled: !t.enabled }),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['appt-types', productId] }) },
  })
  const name_ = (id: string) => people.data?.find((p) => p.id === id)?.displayName ?? 'Unknown'
  return (
    <div className="card">
      <h2>Appointment types</h2>
      <p className="muted">Each type has its own weekly hours, staff and buffers. Share the public link, or add a lead’s personal link from the lead page flow via the API.</p>
      {types.data?.length === 0 && <p className="muted">None yet.</p>}
      {types.data?.map((t) => (
        <div key={t.id} style={{ borderTop: '1px solid var(--line)', padding: '.5rem 0' }}>
          <strong>{t.name}</strong> <span className="muted">· {t.durationMinutes} min · {t.timeZone} · {t.staffUserIds.map(name_).join(', ')}</span>{' '}
          <span className={t.enabled ? 'badge ok' : 'badge warn'}>{t.enabled ? 'Open for booking' : 'Switched off'}</span>
          <div><code>{location.origin}/book/{t.publicKey}</code></div>
          <button className="secondary" onClick={() => setEditing(editing === t.id ? '' : t.id)}>Weekly hours</button>{' '}
          <button className="link" onClick={() => toggle.mutate(t)}>{t.enabled ? 'Switch off' : 'Switch on'}</button>
          {editing === t.id && <Hours typeId={t.id} />}
        </div>
      ))}
      <h3>New type</h3>
      <div className="row">
        <label>Name<input value={name} onChange={(e) => setName(e.target.value)} /></label>
        <label>Minutes<input type="number" min={5} value={dur} onChange={(e) => setDur(e.target.value)} /></label>
        <label>Time zone (IANA)<input value={zone} onChange={(e) => setZone(e.target.value)} /></label>
        <label>Buffer before<input type="number" min={0} value={before} onChange={(e) => setBefore(e.target.value)} /></label>
        <label>Buffer after<input type="number" min={0} value={after} onChange={(e) => setAfter(e.target.value)} /></label>
      </div>
      <div className="row">
        <label>Who can host it (hold Ctrl to pick several)
          <select multiple value={staff} onChange={(e) => setStaff([...e.target.selectedOptions].map((o) => o.value))}>
            {people.data?.map((p) => <option key={p.id} value={p.id}>{p.displayName}</option>)}
          </select>
        </label>
        <button disabled={!name.trim() || staff.length === 0 || create.isPending} onClick={() => create.mutate()}>Create</button>
      </div>
      {err(types.error ?? create.error ?? toggle.error) && <p className="error" role="alert">{err(types.error ?? create.error ?? toggle.error)}</p>}
    </div>
  )
}

function Hours({ typeId }: { typeId: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['avail', typeId], queryFn: () => api.get<Avail>(`/api/v1/scheduling/appointment-types/${typeId}/availability`) })
  const [rows, setRows] = useState<Avail['rules'] | null>(null); const [closed, setClosed] = useState<string | null>(null)
  const rules = rows ?? q.data?.rules ?? []; const closedText = closed ?? (q.data?.closedDates ?? []).join(', ')
  const save = useMutation({
    mutationFn: () => api.put(`/api/v1/scheduling/appointment-types/${typeId}/availability`, {
      rules, closedDates: closedText.split(/[,\s]+/).filter(Boolean),
    }),
    onSuccess: () => { setRows(null); setClosed(null); void qc.invalidateQueries({ queryKey: ['avail', typeId] }) },
  })
  const set = (i: number, patch: Partial<Avail['rules'][number]>) => setRows(rules.map((r, j) => (j === i ? { ...r, ...patch } : r)))
  return (
    <div style={{ marginTop: '.5rem' }}>
      {rules.map((r, i) => (
        <div className="row" key={i}>
          <label>Day<select value={r.dayOfWeek} onChange={(e) => set(i, { dayOfWeek: Number(e.target.value) })}>{days.map((d, n) => <option key={d} value={n}>{d}</option>)}</select></label>
          <label>From<input value={r.start} onChange={(e) => set(i, { start: e.target.value })} placeholder="09:00" /></label>
          <label>To<input value={r.end} onChange={(e) => set(i, { end: e.target.value })} placeholder="17:00" /></label>
          <button className="link" onClick={() => setRows(rules.filter((_, j) => j !== i))}>Remove</button>
        </div>
      ))}
      <div className="row">
        <button className="secondary" onClick={() => setRows([...rules, { dayOfWeek: 1, start: '09:00', end: '17:00' }])}>Add window</button>
        <label>Closed dates (YYYY-MM-DD, comma separated)<input value={closedText} onChange={(e) => setClosed(e.target.value)} /></label>
        <button disabled={save.isPending} onClick={() => save.mutate()}>Save hours</button>
      </div>
      <p className="muted">Hours are in the type’s time zone. Daylight saving is handled automatically.</p>
      {err(save.error) && <p className="error" role="alert">{err(save.error)}</p>}
    </div>
  )
}

function Upcoming({ productId }: { productId: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['bookings', productId], queryFn: () => api.get<BookingRow[]>(`/api/v1/scheduling/bookings?productId=${productId}`) })
  const cancel = useMutation({
    mutationFn: (id: string) => api.post(`/api/v1/scheduling/bookings/${id}/cancel`, { reason: 'Cancelled by staff' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['bookings', productId] }),
  })
  return (
    <div className="card">
      <h2>Upcoming bookings</h2>
      {q.data?.length === 0 && <p className="muted">No bookings in the next month.</p>}
      {!!q.data?.length && (
        <table>
          <thead><tr><th>When</th><th>Attendee</th><th /></tr></thead>
          <tbody>{q.data.map((b) => (
            <tr key={b.id}><td>{new Date(b.start).toLocaleString()}</td><td>{b.attendeeName} <span className="muted">{b.attendeeEmail}</span></td>
              <td><button className="link" onClick={() => cancel.mutate(b.id)}>Cancel</button></td></tr>
          ))}</tbody>
        </table>
      )}
      {err(cancel.error) && <p className="error" role="alert">{err(cancel.error)}</p>}
    </div>
  )
}
