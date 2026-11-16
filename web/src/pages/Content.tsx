import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../api.ts'
import { useAuth } from '../auth.tsx'

interface ItemSummary { id: string; productId: string; campaignId?: string; title: string; channel: string; format: string; language?: string; currentVersion: number; approvedVersion?: number; status: string; scheduledFor?: string }
interface ItemDetail { item: ItemSummary; current: { number: number; title: string; body: string; note?: string; sourceDraftId?: string }; versions: { number: number; note?: string; approved: boolean; createdAt: string }[] }
interface Campaign { id: string; name: string; objective: string; status: string; budgetUsd?: number; startDate?: string; endDate?: string; channels: string }

const statusClass = (s: string) => (['Approved', 'Scheduled', 'Published'].includes(s) ? 'badge ok' : ['Rejected', 'Cancelled', 'PublishFailed'].includes(s) ? 'badge warn' : 'badge')

function Err({ e }: { e: unknown }) {
  if (!(e instanceof ApiError)) return null
  return <div className="error" role="alert">{e.message}{e.violations.length > 0 && <ul>{e.violations.map((v) => <li key={v}>{v}</li>)}</ul>}</div>
}

// ---------------- per-product content tab ----------------

export function ContentTab({ productId }: { productId: string }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const [open, setOpen] = useState<string | null>(null)
  const list = useQuery({ queryKey: ['content', productId], queryFn: () => api.get<ItemSummary[]>(`/api/v1/content?productId=${productId}`) })
  const formats = useQuery({ queryKey: ['formats'], queryFn: () => api.get<string[]>('/api/v1/content-formats') })
  const campaigns = useQuery({ queryKey: ['campaigns', productId], queryFn: () => api.get<Campaign[]>(`/api/v1/products/${productId}/campaigns`) })
  const [f, setF] = useState({ title: '', channel: 'facebook', format: 'Post', body: '', campaignId: '' })
  const create = useMutation({
    mutationFn: () => api.post<ItemSummary>(`/api/v1/products/${productId}/content`, { ...f, campaignId: f.campaignId || null }),
    onSuccess: (r) => { setF({ ...f, title: '', body: '' }); setOpen(r.id); void qc.invalidateQueries({ queryKey: ['content'] }) },
  })

  if (open) return <ContentEditor id={open} onBack={() => setOpen(null)} />
  return (
    <>
      {list.data?.length === 0 && <p className="muted">No content yet.</p>}
      <table>
        <thead><tr><th>Title</th><th>Channel</th><th>Format</th><th>Status</th><th>Version</th><th>Scheduled</th></tr></thead>
        <tbody>
          {list.data?.map((i) => (
            <tr key={i.id}>
              <td><button className="link" onClick={() => setOpen(i.id)}>{i.title}</button></td><td>{i.channel}</td><td>{i.format}</td>
              <td><span className={statusClass(i.status)}>{i.status}</span></td><td>v{i.currentVersion}{i.approvedVersion ? ` (approved v${i.approvedVersion})` : ''}</td>
              <td>{i.scheduledFor ? new Date(i.scheduledFor).toLocaleString() : ''}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {can('content.write') && (
        <form className="card" onSubmit={(e: FormEvent) => { e.preventDefault(); create.mutate() }}>
          <h2>New content</h2>
          <p className="muted">Tip: generate a draft in the AI studio first, then copy it here, or write it yourself.</p>
          <div className="row">
            <label>Title<input value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} required maxLength={300} /></label>
            <label>Channel<input value={f.channel} onChange={(e) => setF({ ...f, channel: e.target.value })} required /></label>
            <label>Format<select value={f.format} onChange={(e) => setF({ ...f, format: e.target.value })}>{formats.data?.map((x) => <option key={x}>{x}</option>)}</select></label>
            <label>Campaign<select value={f.campaignId} onChange={(e) => setF({ ...f, campaignId: e.target.value })}><option value="">None</option>{campaigns.data?.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
          </div>
          <label>Text<textarea rows={6} value={f.body} onChange={(e) => setF({ ...f, body: e.target.value })} /></label>
          <button type="submit" disabled={create.isPending}>Create draft</button>
          <Err e={create.error} />
        </form>
      )}
    </>
  )
}

function ContentEditor({ id, onBack }: { id: string; onBack: () => void }) {
  const q = useQuery({ queryKey: ['content-item', id], queryFn: () => api.get<ItemDetail>(`/api/v1/content/${id}`) })
  if (!q.data) return <p className="muted">Loading…</p>
  return <EditorBody key={`${id}-${q.data.item.currentVersion}-${q.data.item.status}`} d={q.data} onBack={onBack} />
}

function EditorBody({ d, onBack }: { d: ItemDetail; onBack: () => void }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const it = d.item
  const [title, setTitle] = useState(d.current.title)
  const [body, setBody] = useState(d.current.body)
  const [note, setNote] = useState('')
  const [at, setAt] = useState('')
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['content-item', it.id] }); void qc.invalidateQueries({ queryKey: ['content'] }); void qc.invalidateQueries({ queryKey: ['approvals'] }); void qc.invalidateQueries({ queryKey: ['calendar'] }) }
  const save = useMutation({ mutationFn: () => api.post(`/api/v1/content/${it.id}/versions`, { title, body, note: note || null, baseVersion: it.currentVersion }), onSuccess: refresh })
  const submit = useItemAction(it.id, 'submit', refresh)
  const withdraw = useItemAction(it.id, 'withdraw', refresh)
  const cancel = useItemAction(it.id, 'cancel', refresh)
  const unschedule = useItemAction(it.id, 'unschedule', refresh)
  const schedule = useMutation({ mutationFn: () => api.post(`/api/v1/content/${it.id}/schedule`, { at: new Date(at).toISOString() }), onSuccess: refresh })
  const canWrite = can('content.write') && it.status !== 'Cancelled'
  const dirty = title !== d.current.title || body !== d.current.body
  const error = save.error ?? submit.error ?? withdraw.error ?? cancel.error ?? schedule.error ?? unschedule.error

  return (
    <>
      <button className="link" onClick={onBack}>← Back to content</button>
      <h2>{it.title} <span className="muted">v{it.currentVersion}</span> <span className={statusClass(it.status)}>{it.status}</span></h2>
      {it.status === 'Scheduled' && <p>Scheduled for <strong>{new Date(it.scheduledFor!).toLocaleString()}</strong>. Editing will cancel the schedule and require a new approval.</p>}
      {it.status === 'Approved' && <p className="muted">Approved as v{it.approvedVersion}. Editing creates a new version that needs approval again.</p>}
      {it.status === 'InReview' && <p className="muted">Waiting for approval. Editing will withdraw the request.</p>}
      {it.status === 'Publishing' && <p className="muted">Being published now. It can be edited once publishing finishes.</p>}
      {it.status === 'Published' && <p className="muted">Live. Editing creates a new version that needs approval before it can go out.</p>}
      {it.status === 'PublishFailed' && <p className="error">Publishing failed. See Publishing, Needs attention, to retry or record it by hand.</p>}
      <form className="card" onSubmit={(e: FormEvent) => { e.preventDefault(); save.mutate() }}>
        <label>Title<input value={title} onChange={(e) => setTitle(e.target.value)} disabled={!canWrite} /></label>
        <label>Text<textarea rows={12} value={body} onChange={(e) => setBody(e.target.value)} disabled={!canWrite} /></label>
        {canWrite && (
          <div className="row">
            <label>What changed?<input value={note} onChange={(e) => setNote(e.target.value)} /></label>
            <button type="submit" disabled={!dirty || save.isPending}>Save as v{it.currentVersion + 1}</button>
          </div>
        )}
      </form>
      <div className="row">
        {canWrite && it.status === 'Draft' && !dirty && <button onClick={() => submit.mutate()}>Submit v{it.currentVersion} for approval</button>}
        {canWrite && it.status === 'InReview' && <button className="secondary" onClick={() => withdraw.mutate()}>Withdraw from review</button>}
        {can('publishing.manage') && it.status === 'Approved' && it.approvedVersion === it.currentVersion && (
          <>
            <label>Publish at<input type="datetime-local" value={at} onChange={(e) => setAt(e.target.value)} /></label>
            <button disabled={!at} onClick={() => schedule.mutate()}>Schedule</button>
          </>
        )}
        {can('publishing.manage') && it.status === 'Scheduled' && <button className="secondary" onClick={() => unschedule.mutate()}>Unschedule</button>}
        {canWrite && <button className="danger" onClick={() => { if (confirm('Cancel this content? This cannot be undone.')) cancel.mutate() }}>Cancel content</button>}
      </div>
      {dirty && canWrite && <p className="muted">You have unsaved changes; save them before submitting.</p>}
      <Err e={error} />
      <p className="muted">Versions: {d.versions.map((v) => `v${v.number}${v.approved ? ' ✔ approved' : ''}${v.note ? ` — ${v.note}` : ''}`).join(' · ')}</p>
      <p className="muted">Scheduled items are published at their time to the product’s connected channel. See the Publishing page for status and anything that needs attention.</p>
    </>
  )
}

function useItemAction(id: string, verb: string, onSuccess: () => void) {
  return useMutation({ mutationFn: () => api.post(`/api/v1/content/${id}/${verb}`), onSuccess })
}

// ---------------- campaigns ----------------

export function CampaignsTab({ productId }: { productId: string }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const list = useQuery({ queryKey: ['campaigns', productId], queryFn: () => api.get<Campaign[]>(`/api/v1/products/${productId}/campaigns`) })
  const [f, setF] = useState({ name: '', objective: '', channels: '', budgetUsd: '', startDate: '', endDate: '' })
  const create = useMutation({
    mutationFn: () => api.post(`/api/v1/products/${productId}/campaigns`, { name: f.name, objective: f.objective, channels: f.channels, budgetUsd: f.budgetUsd ? Number(f.budgetUsd) : null, startDate: f.startDate || null, endDate: f.endDate || null }),
    onSuccess: () => { setF({ name: '', objective: '', channels: '', budgetUsd: '', startDate: '', endDate: '' }); void qc.invalidateQueries({ queryKey: ['campaigns', productId] }) },
  })
  const status = useMutation({ mutationFn: (v: { id: string; s: string }) => api.post(`/api/v1/campaigns/${v.id}/status/${v.s}`), onSuccess: () => void qc.invalidateQueries({ queryKey: ['campaigns', productId] }) })
  return (
    <>
      {list.data?.length === 0 && <p className="muted">No campaigns yet.</p>}
      <table>
        <thead><tr><th>Name</th><th>Objective</th><th>Dates</th><th>Budget</th><th>Status</th><th /></tr></thead>
        <tbody>
          {list.data?.map((c) => (
            <tr key={c.id}>
              <td>{c.name}</td><td>{c.objective}</td><td>{c.startDate ?? ''} {c.endDate ? `→ ${c.endDate}` : ''}</td><td>{c.budgetUsd != null ? `$${c.budgetUsd}` : ''}</td>
              <td><span className="badge">{c.status}</span></td>
              <td>{can('campaigns.manage') && !['Completed', 'Cancelled'].includes(c.status) && ['Active', 'Paused', 'Completed', 'Cancelled'].filter((s) => s !== c.status).map((s) => <button key={s} className="link" onClick={() => status.mutate({ id: c.id, s })}>{s.toLowerCase()}</button>)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <Err e={status.error} />
      {can('campaigns.manage') && (
        <form className="card" onSubmit={(e: FormEvent) => { e.preventDefault(); create.mutate() }}>
          <h2>New campaign</h2>
          <p className="muted">A budget here is a plan. Nothing is spent without a separate approval.</p>
          <div className="row">
            <label>Name<input value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} required /></label>
            <label>Objective<input value={f.objective} onChange={(e) => setF({ ...f, objective: e.target.value })} /></label>
            <label>Channels<input value={f.channels} onChange={(e) => setF({ ...f, channels: e.target.value })} /></label>
          </div>
          <div className="row">
            <label>Start<input type="date" value={f.startDate} onChange={(e) => setF({ ...f, startDate: e.target.value })} /></label>
            <label>End<input type="date" value={f.endDate} onChange={(e) => setF({ ...f, endDate: e.target.value })} /></label>
            <label>Budget (USD)<input type="number" min={0} step="0.01" value={f.budgetUsd} onChange={(e) => setF({ ...f, budgetUsd: e.target.value })} /></label>
          </div>
          <button type="submit" disabled={create.isPending}>Create campaign</button>
          <Err e={create.error} />
        </form>
      )}
    </>
  )
}

// ---------------- calendar (all products) ----------------

export function CalendarPage() {
  const [offset, setOffset] = useState(0)
  const start = new Date(); start.setDate(1); start.setMonth(start.getMonth() + offset); start.setHours(0, 0, 0, 0)
  const end = new Date(start); end.setMonth(end.getMonth() + 1)
  const items = useQuery({ queryKey: ['calendar', start.toISOString()], queryFn: () => api.get<ItemSummary[]>(`/api/v1/calendar?from=${encodeURIComponent(start.toISOString())}&to=${encodeURIComponent(end.toISOString())}`) })
  const approved = useQuery({ queryKey: ['content', 'approved-queue'], queryFn: () => api.get<ItemSummary[]>('/api/v1/content?status=Approved') })
  const byDay = new Map<string, ItemSummary[]>()
  items.data?.forEach((i) => { const k = new Date(i.scheduledFor!).toLocaleDateString(); byDay.set(k, [...(byDay.get(k) ?? []), i]) })

  return (
    <>
      <h1>Calendar</h1>
      <div className="row">
        <button className="secondary" onClick={() => setOffset(offset - 1)}>←</button>
        <strong>{start.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })}</strong>
        <button className="secondary" onClick={() => setOffset(offset + 1)}>→</button>
      </div>
      {byDay.size === 0 && <p className="muted">Nothing scheduled this month.</p>}
      {[...byDay.entries()].map(([day, list]) => (
        <div className="card" key={day}>
          <strong>{day}</strong>
          <ul>{list.map((i) => <li key={i.id}>{new Date(i.scheduledFor!).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })} · {i.channel} · <Link to={`/products/${i.productId}/content`}>{i.title}</Link></li>)}</ul>
        </div>
      ))}
      <h2>Approved, waiting to be scheduled</h2>
      {approved.data?.length === 0 && <p className="muted">None.</p>}
      <ul>{approved.data?.map((i) => <li key={i.id}><Link to={`/products/${i.productId}/content`}>{i.title}</Link> <span className="muted">· {i.channel} · v{i.approvedVersion}</span></li>)}</ul>
    </>
  )
}
