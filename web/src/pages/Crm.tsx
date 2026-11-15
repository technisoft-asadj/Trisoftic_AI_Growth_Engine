import { useState } from 'react'
import { Link, NavLink, Navigate, Route, Routes, useNavigate, useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type Product } from '../api.ts'
import { useAuth } from '../auth.tsx'
import { AppointmentsTab } from './Appointments.tsx'
import { MessagesTab } from './Messaging.tsx'

// ---------- types mirroring the CRM API ----------
interface Money { currency: string; value?: number; weighted?: number; revenue?: number }
interface Dash {
  meta: { asOf: string; truncated: boolean; notes: string[] }
  leads: { created: number; byStatus: Record<string, number>; bySource: { source: string; count: number }[]; attributedToCampaign: number; unknownSource: number }
  funnel: { created: number; contacted: number; qualified: number; converted: number; won: number; contactedRatePct: number | null; qualifiedRatePct: number | null; convertedRatePct: number | null; wonRatePct: number | null; leadToWonPct: number | null }
  responseTime: { sampleSize: number; averageMinutes: number | null; medianMinutes: number | null; leadsNotYetContacted: number }
  pipeline: { openDeals: number; openValueByCurrency: Money[]; pastExpectedClose: number }
  outcomes: { won: number; lost: number; winRatePct: number | null; revenueByCurrency: Money[]; averageSalesCycleDays: number | null }
  followUps: { overdueLeads: number; overdueSalesActivities: number }
}
interface LeadRow { id: string; contactId: string; contactName?: string; productId: string; status: string; source: string; score: number; nextAction?: string; nextActionDue?: string; createdAt: string }
interface LeadDetail {
  lead: { id: string; contactId: string; productId: string; status: string; source: string; score: number; message?: string; nextAction?: string; nextActionDue?: string; convertedOpportunityId?: string; disqualifyReason?: string; createdAt: string }
  contact: { id: string; displayName: string; jobTitle?: string }
  scoreBreakdown: { rule: string; points: number }[]
}
interface ContactRow { id: string; displayName: string; jobTitle?: string; source?: string; methods: { type: string; value: string }[] }
interface ContactDetail {
  contact: { id: string; displayName: string; jobTitle?: string; source?: string; notes?: string }
  company?: { name: string }
  methods: { id: string; type: string; value: string; isPrimary: boolean }[]
  tags: string[]
  consent: { channel: string; purpose: string; status: string; source: string; evidence?: string; recordedAt: string }[]
  interactions: { id: string; type: string; summary: string; occurredAt: string }[]
}
interface Pipeline { id: string; name: string; stages: { id: string; name: string; kind: string; probability: number }[] }
interface Board { pipeline: { id: string; name: string }; columns: { stage: { id: string; name: string; kind: string }; count: number; totalsByCurrency: Money[]; items: { id: string; name: string; value: number; currency: string; probability: number; contactName?: string; nextStep?: string }[] }[] }
interface OppDetail {
  opportunity: { id: string; name: string; value: number; currency: string; probability: number; stage: string; stageKind: string; status: string; pipelineId: string; wonLostReason?: string; revenueAmount?: number; nextStep?: string }
  contact: { id: string; displayName: string }
  history: { from?: string; to?: string; note?: string; changedAt: string }[]
  activities: { id: string; type: string; summary: string; dueAt?: string; completedAt?: string }[]
}
interface FormRow { id: string; name: string; publicKey: string; consentText?: string; enabled: boolean }
interface RuleRow { id: string; name: string; field: string; value?: string; points: number; enabled: boolean }
interface ImportResult { dryRun: boolean; total: number; contactsCreated: number; contactsMatched: number; leadsCreated: number; attachedToOpenLead: number; rowsWithConsent: number; errors: { row: number; message: string }[] }

const dash = '—'
const pct = (v: number | null | undefined) => (v == null ? dash : `${v}%`)
const num = (v: number | null | undefined, unit = '') => (v == null ? dash : `${v}${unit}`)
const money = (v: number, c: string) => `${c} ${v.toLocaleString(undefined, { maximumFractionDigits: 2 })}`
const when = (s?: string) => (s ? new Date(s).toLocaleString() : dash)
const errText = (e: unknown) => (e instanceof ApiError ? e.message : e ? 'Something went wrong' : null)

function useProducts() {
  return useQuery({ queryKey: ['products'], queryFn: () => api.get<Product[]>('/api/v1/products') })
}

function Err({ e }: { e: unknown }) {
  const t = errText(e)
  return t ? <p className="error" role="alert">{t}</p> : null
}

function ProductSelect({ value, onChange, allowAll = true }: { value: string; onChange: (v: string) => void; allowAll?: boolean }) {
  const products = useProducts()
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} aria-label="Product">
      {allowAll && <option value="">All products</option>}
      {products.data?.filter((p) => !p.isArchived).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
    </select>
  )
}

// ---------- shell ----------
export function CrmPage() {
  const { can } = useAuth()
  return (
    <>
      <h1>CRM</h1>
      <div className="tabs">
        <NavLink to="/crm/dashboard">Dashboard</NavLink>
        <NavLink to="/crm/leads">Leads</NavLink>
        <NavLink to="/crm/contacts">Contacts</NavLink>
        <NavLink to="/crm/pipeline">Pipeline</NavLink>
        <NavLink to="/crm/appointments">Appointments</NavLink>
        {can('crm.write') && <NavLink to="/crm/messages">Messages</NavLink>}
        {can('leads.manage') && <NavLink to="/crm/setup">Setup</NavLink>}
      </div>
      <Routes>
        <Route index element={<Navigate to="dashboard" replace />} />
        <Route path="dashboard" element={<DashboardTab />} />
        <Route path="leads" element={<LeadsTab />} />
        <Route path="leads/:id" element={<LeadView />} />
        <Route path="contacts" element={<ContactsTab />} />
        <Route path="contacts/:id" element={<ContactView />} />
        <Route path="pipeline" element={<PipelineTab />} />
        <Route path="deals/:id" element={<DealView />} />
        <Route path="appointments" element={<AppointmentsTab />} />
        <Route path="messages" element={<MessagesTab />} />
        <Route path="setup" element={<SetupTab />} />
      </Routes>
    </>
  )
}

// ---------- dashboard ----------
function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="card tile">
      <span className="muted">{label}</span>
      <strong style={{ fontSize: '1.5rem' }}>{value}</strong>
      {hint && <span className="muted">{hint}</span>}
    </div>
  )
}

function MoneyList({ items, field }: { items: Money[]; field: 'value' | 'weighted' | 'revenue' }) {
  if (items.length === 0) return <>{dash}</>
  // Currencies are shown side by side and never added together.
  return <>{items.map((m) => <div key={m.currency}>{money(m[field] ?? 0, m.currency)}</div>)}</>
}

function DashboardTab() {
  const [productId, setProductId] = useState('')
  const [days, setDays] = useState(30)
  const q = useQuery({
    queryKey: ['crm-dash', productId, days],
    queryFn: () => {
      const to = new Date(); const from = new Date(to.getTime() - days * 86_400_000)
      const p = new URLSearchParams({ from: from.toISOString(), to: to.toISOString() })
      if (productId) p.set('productId', productId)
      return api.get<Dash>(`/api/v1/crm/dashboard?${p}`)
    },
  })
  const d = q.data
  return (
    <>
      <div className="row">
        <label>Product<ProductSelect value={productId} onChange={setProductId} /></label>
        <label>Period
          <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
            <option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option>
          </select>
        </label>
      </div>
      <Err e={q.error} />
      {q.isLoading && <p className="muted">Loading…</p>}
      {d && (
        <>
          <p className="muted">A “{dash}” means there is nothing to measure yet; it is not zero. As of {when(d.meta.asOf)}.{d.meta.truncated && ' Large period: results are truncated.'}</p>
          <div className="grid">
            <Stat label="New leads" value={String(d.leads.created)} hint={`${d.leads.attributedToCampaign} from a campaign · ${d.leads.unknownSource} unknown source`} />
            <Stat label="Median first response" value={num(d.responseTime.medianMinutes, ' min')} hint={`${d.responseTime.leadsNotYetContacted} not yet contacted`} />
            <Stat label="Win rate" value={pct(d.outcomes.winRatePct)} hint={`${d.outcomes.won} won · ${d.outcomes.lost} lost`} />
            <Stat label="Avg sales cycle" value={num(d.outcomes.averageSalesCycleDays, ' days')} />
            <Stat label="Overdue follow-ups" value={String(d.followUps.overdueLeads + d.followUps.overdueSalesActivities)} hint={`${d.followUps.overdueLeads} leads · ${d.followUps.overdueSalesActivities} deal tasks`} />
            <Stat label="Deals past expected close" value={String(d.pipeline.pastExpectedClose)} />
          </div>
          <div className="card">
            <h2>Funnel</h2>
            <table>
              <thead><tr><th>Step</th><th>Count</th><th>Rate from previous step</th></tr></thead>
              <tbody>
                <tr><td>Created</td><td>{d.funnel.created}</td><td>{dash}</td></tr>
                <tr><td>Contacted</td><td>{d.funnel.contacted}</td><td>{pct(d.funnel.contactedRatePct)}</td></tr>
                <tr><td>Qualified</td><td>{d.funnel.qualified}</td><td>{pct(d.funnel.qualifiedRatePct)}</td></tr>
                <tr><td>Converted to deal</td><td>{d.funnel.converted}</td><td>{pct(d.funnel.convertedRatePct)}</td></tr>
                <tr><td>Won</td><td>{d.funnel.won}</td><td>{pct(d.funnel.wonRatePct)}</td></tr>
              </tbody>
            </table>
            <p className="muted">Lead to won: {pct(d.funnel.leadToWonPct)}</p>
          </div>
          <div className="grid">
            <div className="card"><h2>Open pipeline ({d.pipeline.openDeals} deals)</h2>
              <p className="muted">Total</p><MoneyList items={d.pipeline.openValueByCurrency} field="value" />
              <p className="muted">Probability-weighted</p><MoneyList items={d.pipeline.openValueByCurrency} field="weighted" />
            </div>
            <div className="card"><h2>Revenue won</h2><MoneyList items={d.outcomes.revenueByCurrency} field="revenue" /></div>
            <div className="card"><h2>Lead sources</h2>
              {d.leads.bySource.length === 0 ? <p className="muted">No leads in this period.</p> : d.leads.bySource.map((s) => <div key={s.source}>{s.source}: {s.count}</div>)}
            </div>
          </div>
        </>
      )}
    </>
  )
}

// ---------- leads ----------
function LeadsTab() {
  const [status, setStatus] = useState(''); const [productId, setProductId] = useState('')
  const [overdue, setOverdue] = useState(false); const [unassigned, setUnassigned] = useState(false)
  const q = useQuery({
    queryKey: ['leads', status, productId, overdue, unassigned],
    queryFn: () => {
      const p = new URLSearchParams()
      if (status) p.set('status', status); if (productId) p.set('productId', productId)
      if (overdue) p.set('overdue', 'true'); if (unassigned) p.set('unassigned', 'true')
      return api.get<{ items: LeadRow[]; nextCursor?: string }>(`/api/v1/crm/leads?${p}`)
    },
  })
  return (
    <>
      <div className="row">
        <label>Product<ProductSelect value={productId} onChange={setProductId} /></label>
        <label>Status
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Any</option>{['New', 'Contacted', 'Qualified', 'Disqualified', 'Converted'].map((s) => <option key={s}>{s}</option>)}
          </select>
        </label>
        <label className="inline"><input type="checkbox" checked={overdue} onChange={(e) => setOverdue(e.target.checked)} /> Overdue follow-up</label>
        <label className="inline"><input type="checkbox" checked={unassigned} onChange={(e) => setUnassigned(e.target.checked)} /> Unassigned</label>
      </div>
      <Err e={q.error} />
      {q.data?.items.length === 0 && <p className="muted">No leads match.</p>}
      {!!q.data?.items.length && (
        <table>
          <thead><tr><th>Contact</th><th>Status</th><th>Score</th><th>Source</th><th>Next action</th><th>Created</th></tr></thead>
          <tbody>
            {q.data.items.map((l) => (
              <tr key={l.id}>
                <td><Link to={`/crm/leads/${l.id}`}>{l.contactName ?? 'Unknown'}</Link></td>
                <td><span className="badge">{l.status}</span></td><td>{l.score}</td><td>{l.source}</td>
                <td>{l.nextAction ?? dash}{l.nextActionDue && <span className="muted"> · due {when(l.nextActionDue)}</span>}</td>
                <td>{when(l.createdAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {q.data?.nextCursor && <p className="muted">Showing the newest 50. Narrow the filters to see others.</p>}
    </>
  )
}

function LeadView() {
  const { id } = useParams(); const qc = useQueryClient(); const nav = useNavigate()
  const { can } = useAuth()
  const q = useQuery({ queryKey: ['lead', id], queryFn: () => api.get<LeadDetail>(`/api/v1/crm/leads/${id}`) })
  const [note, setNote] = useState(''); const [reason, setReason] = useState('')
  const [nextText, setNextText] = useState(''); const [nextDue, setNextDue] = useState('')
  const [dealValue, setDealValue] = useState('0'); const [currency, setCurrency] = useState('USD')
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['lead', id] }); void qc.invalidateQueries({ queryKey: ['leads'] }) }
  const setStatus = useMutation({
    mutationFn: (status: string) => api.post(`/api/v1/crm/leads/${id}/status`, { status, note: note || null, reason: reason || null }),
    onSuccess: () => { setNote(''); setReason(''); refresh() },
  })
  const saveNext = useMutation({
    mutationFn: () => api.put(`/api/v1/crm/leads/${id}/next-action`, { text: nextText || null, due: nextDue ? new Date(nextDue).toISOString() : null }),
    onSuccess: refresh,
  })
  const rescore = useMutation({ mutationFn: () => api.post(`/api/v1/crm/leads/${id}/rescore`), onSuccess: refresh })
  const convert = useMutation({
    mutationFn: () => api.post<{ id: string }>(`/api/v1/crm/leads/${id}/convert`, { value: Number(dealValue) || 0, currency }),
    onSuccess: (o) => { refresh(); nav(`/crm/deals/${o.id}`) },
  })

  if (q.isLoading) return <p className="muted">Loading…</p>
  if (!q.data) return <><Err e={q.error} /><Link to="/crm/leads">Back to leads</Link></>
  const { lead, contact, scoreBreakdown } = q.data
  const manage = can('leads.manage'); const sales = can('sales.manage')
  const open = ['New', 'Contacted', 'Qualified'].includes(lead.status)
  const mutErr = setStatus.error ?? saveNext.error ?? rescore.error ?? convert.error

  return (
    <>
      <p><Link to="/crm/leads">← Leads</Link></p>
      <div className="card">
        <h2><Link to={`/crm/contacts/${contact.id}`}>{contact.displayName}</Link> <span className="badge">{lead.status}</span></h2>
        <p className="muted">{contact.jobTitle ?? 'No job title'} · source {lead.source} · created {when(lead.createdAt)}</p>
        {lead.message && <p>“{lead.message}”</p>}
        {lead.disqualifyReason && <p className="muted">Disqualified: {lead.disqualifyReason}</p>}
        {lead.convertedOpportunityId && <p><Link to={`/crm/deals/${lead.convertedOpportunityId}`}>Open the deal</Link></p>}
        <Err e={mutErr} />
      </div>

      <div className="card">
        <h2>Score {lead.score}</h2>
        {scoreBreakdown.length === 0 ? <p className="muted">No score rules matched.</p> : <ul>{scoreBreakdown.map((h, i) => <li key={i}>{h.rule}: {h.points > 0 ? '+' : ''}{h.points}</li>)}</ul>}
        {manage && open && <button className="secondary" disabled={rescore.isPending} onClick={() => rescore.mutate()}>Recalculate</button>}
      </div>

      {manage && (
        <div className="card">
          <h2>Next action</h2>
          <p className="muted">{lead.nextAction ?? 'None set'}{lead.nextActionDue && ` · due ${when(lead.nextActionDue)}`}</p>
          {open && (
            <div className="row">
              <label>What<input value={nextText} onChange={(e) => setNextText(e.target.value)} maxLength={500} /></label>
              <label>Due<input type="datetime-local" value={nextDue} onChange={(e) => setNextDue(e.target.value)} /></label>
              <button disabled={saveNext.isPending} onClick={() => saveNext.mutate()}>Save</button>
            </div>
          )}
        </div>
      )}

      {manage && (
        <div className="card">
          <h2>Move this lead</h2>
          <div className="row">
            <label>Note (for qualifying)<input value={note} onChange={(e) => setNote(e.target.value)} /></label>
            <label>Reason (for disqualifying)<input value={reason} onChange={(e) => setReason(e.target.value)} /></label>
          </div>
          <div className="row">
            {lead.status === 'New' && <button onClick={() => setStatus.mutate('Contacted')}>Mark contacted</button>}
            {(lead.status === 'New' || lead.status === 'Contacted') && <button onClick={() => setStatus.mutate('Qualified')}>Qualify</button>}
            {open && <button className="danger" onClick={() => setStatus.mutate('Disqualified')}>Disqualify</button>}
            {lead.status === 'Disqualified' && <button className="secondary" onClick={() => setStatus.mutate('New')}>Reopen</button>}
          </div>
        </div>
      )}

      {sales && lead.status === 'Qualified' && (
        <div className="card">
          <h2>Convert to deal</h2>
          <div className="row">
            <label>Expected value<input type="number" min={0} value={dealValue} onChange={(e) => setDealValue(e.target.value)} /></label>
            <label>Currency<input value={currency} maxLength={3} onChange={(e) => setCurrency(e.target.value.toUpperCase())} /></label>
            <button disabled={convert.isPending} onClick={() => convert.mutate()}>Create deal</button>
          </div>
          <p className="muted">The deal starts in the first stage of the product’s pipeline.</p>
        </div>
      )}
    </>
  )
}

// ---------- contacts ----------
function ContactsTab() {
  const [text, setText] = useState(''); const [consentEmail, setConsentEmail] = useState(false)
  const q = useQuery({
    queryKey: ['contacts', text, consentEmail],
    queryFn: () => {
      const p = new URLSearchParams()
      if (text.trim()) p.set('q', text.trim()); if (consentEmail) p.set('consentChannel', 'Email')
      return api.get<{ items: ContactRow[]; nextCursor?: string }>(`/api/v1/crm/contacts?${p}`)
    },
  })
  return (
    <>
      <div className="row">
        <label>Search<input placeholder="Name, email, phone or company" value={text} onChange={(e) => setText(e.target.value)} /></label>
        <label className="inline"><input type="checkbox" checked={consentEmail} onChange={(e) => setConsentEmail(e.target.checked)} /> Has email marketing consent</label>
      </div>
      <Err e={q.error} />
      {q.data?.items.length === 0 && <p className="muted">No contacts match.</p>}
      {!!q.data?.items.length && (
        <table>
          <thead><tr><th>Name</th><th>Contact details</th><th>Job title</th><th>Source</th></tr></thead>
          <tbody>
            {q.data.items.map((c) => (
              <tr key={c.id}>
                <td><Link to={`/crm/contacts/${c.id}`}>{c.displayName}</Link></td>
                <td>{c.methods.map((m) => m.value).join(', ') || dash}</td><td>{c.jobTitle ?? dash}</td><td>{c.source ?? dash}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}

function ContactView() {
  const { id } = useParams(); const qc = useQueryClient(); const { can } = useAuth()
  const q = useQuery({ queryKey: ['contact', id], queryFn: () => api.get<ContactDetail>(`/api/v1/crm/contacts/${id}`), retry: false })
  const [channel, setChannel] = useState('Email'); const [purpose, setPurpose] = useState('Marketing')
  const [status, setStatus] = useState('Granted'); const [source, setSource] = useState(''); const [evidence, setEvidence] = useState('')
  const [note, setNote] = useState('')
  const refresh = () => void qc.invalidateQueries({ queryKey: ['contact', id] })
  const record = useMutation({
    mutationFn: () => api.post(`/api/v1/crm/contacts/${id}/consent`, { channel, purpose, status, source, evidence: evidence || null }),
    onSuccess: () => { setSource(''); setEvidence(''); refresh() },
  })
  const addNote = useMutation({
    mutationFn: () => api.post(`/api/v1/crm/contacts/${id}/interactions`, { type: 'Note', summary: note }),
    onSuccess: () => { setNote(''); refresh() },
  })

  if (q.isLoading) return <p className="muted">Loading…</p>
  if (!q.data) return <><Err e={q.error} /><Link to="/crm/contacts">Back to contacts</Link></>
  const d = q.data; const write = can('crm.write')
  return (
    <>
      <p><Link to="/crm/contacts">← Contacts</Link></p>
      <div className="card">
        <h2>{d.contact.displayName}</h2>
        <p className="muted">{d.contact.jobTitle ?? 'No job title'}{d.company && ` · ${d.company.name}`}{d.contact.source && ` · source ${d.contact.source}`}</p>
        <ul>{d.methods.map((m) => <li key={m.id}>{m.type}: {m.value}{m.isPrimary && ' (primary)'}</li>)}</ul>
        {d.tags.length > 0 && <p>{d.tags.map((t) => <span key={t} className="badge">{t}</span>)}</p>}
        {d.contact.notes && <p>{d.contact.notes}</p>}
      </div>

      <div className="card">
        <h2>Consent</h2>
        <p className="muted">The latest record for each channel and purpose decides whether we may message this person. History is never edited.</p>
        {d.consent.length === 0 ? <p className="muted">No consent recorded: marketing messages are not allowed.</p> : (
          <table>
            <thead><tr><th>Channel</th><th>Purpose</th><th>Status</th><th>Source</th><th>Recorded</th></tr></thead>
            <tbody>{d.consent.map((k, i) => (
              <tr key={i}><td>{k.channel}</td><td>{k.purpose}</td>
                <td><span className={k.status === 'Granted' ? 'badge ok' : 'badge warn'}>{k.status}</span></td>
                <td>{k.source}{k.evidence && <div className="muted">{k.evidence}</div>}</td><td>{when(k.recordedAt)}</td></tr>
            ))}</tbody>
          </table>
        )}
        {write && (
          <>
            <div className="row">
              <label>Channel<select value={channel} onChange={(e) => setChannel(e.target.value)}>{['Email', 'Phone', 'WhatsApp', 'Sms'].map((c) => <option key={c}>{c}</option>)}</select></label>
              <label>Purpose<select value={purpose} onChange={(e) => setPurpose(e.target.value)}>{['Marketing', 'Transactional'].map((c) => <option key={c}>{c}</option>)}</select></label>
              <label>Decision<select value={status} onChange={(e) => setStatus(e.target.value)}><option>Granted</option><option>Withdrawn</option></select></label>
            </div>
            <div className="row">
              <label>Where it came from<input placeholder="e.g. signed form, phone call" value={source} onChange={(e) => setSource(e.target.value)} /></label>
              <label>Evidence (required to grant)<input value={evidence} onChange={(e) => setEvidence(e.target.value)} /></label>
              <button disabled={!source.trim() || record.isPending} onClick={() => record.mutate()}>Record</button>
            </div>
            <Err e={record.error} />
          </>
        )}
      </div>

      <div className="card">
        <h2>Timeline</h2>
        {write && (
          <div className="row">
            <label>Add a note<input value={note} onChange={(e) => setNote(e.target.value)} maxLength={2000} /></label>
            <button disabled={!note.trim() || addNote.isPending} onClick={() => addNote.mutate()}>Add</button>
          </div>
        )}
        <Err e={addNote.error} />
        {d.interactions.length === 0 ? <p className="muted">Nothing yet.</p> : (
          <table><tbody>{d.interactions.map((i) => <tr key={i.id}><td className="muted">{when(i.occurredAt)}</td><td><span className="badge">{i.type}</span></td><td>{i.summary}</td></tr>)}</tbody></table>
        )}
      </div>
    </>
  )
}

// ---------- pipeline ----------
function PipelineTab() {
  const pipelines = useQuery({ queryKey: ['pipelines'], queryFn: () => api.get<Pipeline[]>('/api/v1/crm/pipelines') })
  const [chosen, setChosen] = useState('')
  const pid = chosen || pipelines.data?.[0]?.id || ''
  const board = useQuery({ queryKey: ['board', pid], enabled: !!pid, queryFn: () => api.get<Board>(`/api/v1/crm/pipelines/${pid}/board`) })
  return (
    <>
      <div className="row">
        <label>Pipeline
          <select value={pid} onChange={(e) => setChosen(e.target.value)}>{pipelines.data?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select>
        </label>
      </div>
      <Err e={pipelines.error ?? board.error} />
      <p className="muted">Totals are shown per currency and never added across currencies. Closed deals leave the board after 30 days.</p>
      <div style={{ display: 'flex', gap: '.75rem', overflowX: 'auto', alignItems: 'flex-start' }}>
        {board.data?.columns.map((c) => (
          <div key={c.stage.id} className="card" style={{ minWidth: 220, flex: '0 0 220px', margin: 0 }}>
            <strong>{c.stage.name}</strong> <span className="badge">{c.count}</span>
            <div className="muted">{c.totalsByCurrency.length === 0 ? dash : c.totalsByCurrency.map((t) => <div key={t.currency}>{money(t.value ?? 0, t.currency)}</div>)}</div>
            {c.items.map((o) => (
              <div key={o.id} style={{ borderTop: '1px solid var(--line)', marginTop: '.5rem', paddingTop: '.5rem' }}>
                <Link to={`/crm/deals/${o.id}`}>{o.name}</Link>
                <div className="muted">{o.contactName} · {money(o.value, o.currency)} · {o.probability}%</div>
                {o.nextStep && <div className="muted">Next: {o.nextStep}</div>}
              </div>
            ))}
          </div>
        ))}
      </div>
    </>
  )
}

function DealView() {
  const { id } = useParams(); const qc = useQueryClient(); const { can } = useAuth()
  const q = useQuery({ queryKey: ['deal', id], queryFn: () => api.get<OppDetail>(`/api/v1/crm/opportunities/${id}`) })
  const pipelines = useQuery({ queryKey: ['pipelines'], queryFn: () => api.get<Pipeline[]>('/api/v1/crm/pipelines') })
  const [stageId, setStageId] = useState(''); const [reason, setReason] = useState(''); const [revenue, setRevenue] = useState('')
  const [type, setType] = useState('Note'); const [summary, setSummary] = useState(''); const [due, setDue] = useState('')
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['deal', id] }); void qc.invalidateQueries({ queryKey: ['board'] }) }
  const move = useMutation({
    mutationFn: (v: { path: 'stage' | 'reopen'; stageId: string }) => api.post(`/api/v1/crm/opportunities/${id}/${v.path}`,
      v.path === 'reopen' ? { stageId: v.stageId } : { stageId: v.stageId, reason: reason || null, revenue: revenue ? Number(revenue) : null }),
    onSuccess: () => { setReason(''); setRevenue(''); setStageId(''); refresh() },
  })
  const log = useMutation({
    mutationFn: () => api.post(`/api/v1/crm/opportunities/${id}/activities`, { type, summary, dueAt: due ? new Date(due).toISOString() : null }),
    onSuccess: () => { setSummary(''); setDue(''); refresh() },
  })
  const complete = useMutation({ mutationFn: (aid: string) => api.post(`/api/v1/crm/activities/${aid}/complete`), onSuccess: refresh })

  if (q.isLoading) return <p className="muted">Loading…</p>
  if (!q.data) return <><Err e={q.error} /><Link to="/crm/pipeline">Back to pipeline</Link></>
  const { opportunity: o, contact, history, activities } = q.data
  const stages = pipelines.data?.find((p) => p.id === o.pipelineId)?.stages ?? []
  const target = stages.find((s) => s.id === stageId)
  const closed = o.status !== 'Open'; const sales = can('sales.manage')

  return (
    <>
      <p><Link to="/crm/pipeline">← Pipeline</Link></p>
      <div className="card">
        <h2>{o.name} <span className="badge">{o.status === 'Open' ? o.stage : o.status}</span></h2>
        <p>{money(o.value, o.currency)} · {o.probability}% · <Link to={`/crm/contacts/${contact.id}`}>{contact.displayName}</Link></p>
        {o.status === 'Won' && <p>Revenue: {o.revenueAmount != null ? money(o.revenueAmount, o.currency) : dash}</p>}
        {o.wonLostReason && <p className="muted">Reason: {o.wonLostReason}</p>}
        {o.nextStep && <p className="muted">Next step: {o.nextStep}</p>}
        <Err e={move.error ?? log.error ?? complete.error} />
      </div>

      {sales && (
        <div className="card">
          <h2>{closed ? 'Reopen' : 'Move'}</h2>
          <div className="row">
            <label>Stage
              <select value={stageId} onChange={(e) => setStageId(e.target.value)}>
                <option value="">Choose…</option>
                {stages.filter((s) => (closed ? s.kind === 'Open' : s.name !== o.stage)).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </select>
            </label>
            {!closed && target?.kind === 'Lost' && <label>Reason (required)<input value={reason} onChange={(e) => setReason(e.target.value)} /></label>}
            {!closed && target?.kind === 'Won' && <label>Actual revenue (optional)<input type="number" min={0} value={revenue} onChange={(e) => setRevenue(e.target.value)} /></label>}
            <button disabled={!stageId || move.isPending} onClick={() => move.mutate({ path: closed ? 'reopen' : 'stage', stageId })}>{closed ? 'Reopen deal' : 'Move deal'}</button>
          </div>
        </div>
      )}

      <div className="card">
        <h2>Activities</h2>
        {sales && !closed && (
          <div className="row">
            <label>Type<select value={type} onChange={(e) => setType(e.target.value)}>{['Note', 'Call', 'Email', 'Meeting', 'Task'].map((t) => <option key={t}>{t}</option>)}</select></label>
            <label>Summary<input value={summary} onChange={(e) => setSummary(e.target.value)} maxLength={2000} /></label>
            {type === 'Task' && <label>Due<input type="datetime-local" value={due} onChange={(e) => setDue(e.target.value)} /></label>}
            <button disabled={!summary.trim() || log.isPending} onClick={() => log.mutate()}>Add</button>
          </div>
        )}
        {activities.length === 0 ? <p className="muted">None yet.</p> : (
          <table><tbody>{activities.map((a) => (
            <tr key={a.id}><td><span className="badge">{a.type}</span></td><td>{a.summary}{a.dueAt && <span className="muted"> · due {when(a.dueAt)}</span>}</td>
              <td>{a.completedAt ? <span className="muted">done {when(a.completedAt)}</span> : sales ? <button className="secondary" onClick={() => complete.mutate(a.id)}>Complete</button> : 'open'}</td></tr>
          ))}</tbody></table>
        )}
      </div>

      <div className="card">
        <h2>Stage history</h2>
        <table><tbody>{history.map((h, i) => <tr key={i}><td className="muted">{when(h.changedAt)}</td><td>{h.from ? `${h.from} → ` : ''}{h.to}</td><td>{h.note}</td></tr>)}</tbody></table>
      </div>
    </>
  )
}

// ---------- setup: lead forms, score rules, CSV import ----------
function SetupTab() {
  const [productId, setProductId] = useState('')
  const products = useProducts()
  const pid = productId || products.data?.find((p) => !p.isArchived)?.id || ''
  return (
    <>
      <div className="row"><label>Product<ProductSelect value={pid} onChange={setProductId} allowAll={false} /></label></div>
      {pid ? <><FormsCard productId={pid} /><ImportCard productId={pid} /></> : <p className="muted">Create a product first.</p>}
      <RulesCard />
    </>
  )
}

function FormsCard({ productId }: { productId: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['forms', productId], queryFn: () => api.get<FormRow[]>(`/api/v1/crm/products/${productId}/lead-forms`) })
  const [name, setName] = useState(''); const [consent, setConsent] = useState('')
  const create = useMutation({
    mutationFn: () => api.post(`/api/v1/crm/products/${productId}/lead-forms`, { name, consentText: consent || null }),
    onSuccess: () => { setName(''); setConsent(''); void qc.invalidateQueries({ queryKey: ['forms', productId] }) },
  })
  const toggle = useMutation({
    mutationFn: (f: FormRow) => api.put(`/api/v1/crm/lead-forms/${f.id}`, { name: f.name, consentText: f.consentText ?? null, enabled: !f.enabled }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['forms', productId] }),
  })
  return (
    <div className="card">
      <h2>Lead forms</h2>
      <p className="muted">Embed a form on any website and POST its fields to the endpoint below. The consent wording is stored as evidence exactly as written here.</p>
      {q.data?.map((f) => (
        <div key={f.id} style={{ borderTop: '1px solid var(--line)', padding: '.5rem 0' }}>
          <strong>{f.name}</strong> <span className={f.enabled ? 'badge ok' : 'badge warn'}>{f.enabled ? 'Enabled' : 'Disabled'}</span>
          <div><code>POST {location.origin}/api/v1/public/lead-forms/{f.publicKey}/submit</code></div>
          <div className="muted">Consent text: {f.consentText ?? 'none (submissions never record consent)'}</div>
          <button className="secondary" onClick={() => toggle.mutate(f)}>{f.enabled ? 'Disable' : 'Enable'}</button>
        </div>
      ))}
      <div className="row">
        <label>New form name<input value={name} onChange={(e) => setName(e.target.value)} /></label>
        <label>Consent wording shown next to the tick box<input value={consent} onChange={(e) => setConsent(e.target.value)} /></label>
        <button disabled={!name.trim() || create.isPending} onClick={() => create.mutate()}>Create</button>
      </div>
      <Err e={q.error ?? create.error ?? toggle.error} />
    </div>
  )
}

function ImportCard({ productId }: { productId: string }) {
  const [file, setFile] = useState<File | null>(null)
  const run = useMutation({
    mutationFn: (dryRun: boolean) => {
      const fd = new FormData(); fd.append('file', file!)
      return api.upload<ImportResult>(`/api/v1/crm/products/${productId}/leads/import?dryRun=${dryRun}`, fd)
    },
  })
  const r = run.data
  return (
    <div className="card">
      <h2>Import leads from CSV</h2>
      <p className="muted">Columns: email or phone (one is required), firstName, lastName, company, jobTitle, message, source, consent (yes/no), consentEvidence. Consent is only recorded when the file says so.</p>
      <div className="row">
        <input type="file" accept=".csv" onChange={(e) => { setFile(e.target.files?.[0] ?? null); run.reset() }} />
        <button className="secondary" disabled={!file || run.isPending} onClick={() => run.mutate(true)}>Preview (nothing saved)</button>
        <button disabled={!file || run.isPending} onClick={() => run.mutate(false)}>Import</button>
      </div>
      <Err e={run.error} />
      {r && (
        <>
          <p><strong>{r.dryRun ? 'Preview' : 'Imported'}:</strong> {r.total} rows · {r.contactsCreated} new contacts · {r.contactsMatched} already existed · {r.leadsCreated} new leads · {r.attachedToOpenLead} already had an open lead · {r.rowsWithConsent} rows with consent</p>
          {r.errors.length > 0 && <div className="error"><ul>{r.errors.slice(0, 20).map((e) => <li key={e.row}>Row {e.row}: {e.message}</li>)}</ul></div>}
        </>
      )}
    </div>
  )
}

function RulesCard() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['rules'], queryFn: () => api.get<RuleRow[]>('/api/v1/crm/score-rules') })
  const fields = ['SourceEquals', 'HasCompany', 'HasPhone', 'JobTitleContains', 'TagEquals', 'MessageContains']
  const [name, setName] = useState(''); const [field, setField] = useState('HasCompany'); const [value, setValue] = useState(''); const [points, setPoints] = useState('10')
  const needsValue = !['HasCompany', 'HasPhone'].includes(field)
  const create = useMutation({
    mutationFn: () => api.post('/api/v1/crm/score-rules', { name, field, value: needsValue ? value : null, points: Number(points) }),
    onSuccess: () => { setName(''); setValue(''); void qc.invalidateQueries({ queryKey: ['rules'] }) },
  })
  const remove = useMutation({
    mutationFn: (id: string) => api.del(`/api/v1/crm/score-rules/${id}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['rules'] }),
  })
  return (
    <div className="card">
      <h2>Lead scoring rules</h2>
      <p className="muted">Each matching rule adds its points. The score is explained on every lead.</p>
      {q.data?.length === 0 && <p className="muted">No rules yet, so every lead scores 0.</p>}
      {!!q.data?.length && (
        <table><tbody>{q.data.map((r) => (
          <tr key={r.id}><td>{r.name}</td><td>{r.field}{r.value && `: ${r.value}`}</td><td>{r.points > 0 ? '+' : ''}{r.points}</td>
            <td><button className="link" onClick={() => remove.mutate(r.id)}>Delete</button></td></tr>
        ))}</tbody></table>
      )}
      <div className="row">
        <label>Name<input value={name} onChange={(e) => setName(e.target.value)} /></label>
        <label>When<select value={field} onChange={(e) => setField(e.target.value)}>{fields.map((f) => <option key={f}>{f}</option>)}</select></label>
        {needsValue && <label>Value<input value={value} onChange={(e) => setValue(e.target.value)} /></label>}
        <label>Points<input type="number" min={-50} max={50} value={points} onChange={(e) => setPoints(e.target.value)} /></label>
        <button disabled={!name.trim() || create.isPending} onClick={() => create.mutate()}>Add rule</button>
      </div>
      <Err e={q.error ?? create.error ?? remove.error} />
    </div>
  )
}
