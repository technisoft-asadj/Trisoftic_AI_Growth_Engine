import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type Product } from '../api.ts'

interface Cond { field: string; op: string; value?: string }
interface Step { kind: string; offsetMinutes?: number; subject?: string; body?: string; purpose?: string; minutes?: number; field?: string; op?: string; value?: string; title?: string; notes?: string; dueInMinutes?: number; assignee?: string; to?: string; message?: string; name?: string }
interface Def { conditions: Cond[]; steps: Step[] }
interface WorkflowRow { id: string; name: string; trigger: string; productId?: string; enabled: boolean; currentVersion: number }
interface Detail { workflow: WorkflowRow; definition: Def; versions: { number: number; note?: string }[] }
interface Catalog { triggers: { type: string; fields: string[] }[]; stepKinds: string[]; ops: string[]; webhooks: string[] }
interface RunRow { id: string; versionNumber: number; eventType: string; status: string; error?: string; createdAt: string; resumeAt?: string }
interface RunDetail { run: RunRow & { workflow: string }; steps: { stepIndex: number; kind: string; status: string; detail?: string; at: string }[] }

const err = (e: unknown) => {
  if (!(e instanceof ApiError)) return e ? 'Something went wrong' : null
  return e.violations.length ? `${e.message}: ${e.violations.join('; ')}` : e.message
}
const blankStep = (kind: string): Step => ({ wait: { kind, minutes: 60 }, if: { kind, field: 'status', op: 'eq', value: 'New' }, task: { kind, title: '', assignee: 'owner', dueInMinutes: 60 },
  notify: { kind, message: '', to: 'owner' }, webhook: { kind, name: '' }, waituntil: { kind, field: 'start', offsetMinutes: -60 },
  email: { kind, purpose: 'transactional', subject: '', body: '' } } as Record<string, Step>)[kind] ?? { kind }

// Display names only; the raw ids stay the saved values. Anything not listed falls back to its raw id.
const LABELS: Record<string, string> = {
  'lead.created': 'A lead arrives', 'lead.assigned': 'A lead is assigned', 'lead.status-changed': 'A lead changes status',
  'opportunity.created': 'A deal is created', 'opportunity.stage-changed': 'A deal changes stage',
  'booking.created': 'A booking is made', 'booking.cancelled': 'A booking is cancelled',
  'analytics.source-stale': 'A data source goes stale', 'budget.threshold-crossed': 'A budget reaches its warning or stop level',
  wait: 'Wait a while', waituntil: 'Wait until a date on the record', if: 'Continue only if…', task: 'Create a task',
  notify: 'Notify someone', webhook: 'Call an n8n webhook', email: 'Send an email',
  eq: 'equals', ne: 'does not equal', contains: 'contains', gt: 'is more than', lt: 'is less than', exists: 'is present', empty: 'is empty',
}
const label = (x: string) => LABELS[x] ?? x

export function AutomationPage() {
  const catalog = useQuery({ queryKey: ['auto-catalog'], queryFn: () => api.get<Catalog>('/api/v1/automation/triggers') })
  const list = useQuery({ queryKey: ['workflows'], queryFn: () => api.get<WorkflowRow[]>('/api/v1/automation/workflows') })
  const [open, setOpen] = useState(''); const [creating, setCreating] = useState(false)
  return (
    <>
      <h1>Automation</h1>
      <p className="muted">Workflows react to things that happen (a lead arrives, a deal moves, a booking is made) and then wait, check, create tasks, notify people, or call n8n. Every run is logged step by step.</p>
      {err(catalog.error ?? list.error) && <p className="error" role="alert">{err(catalog.error ?? list.error)}</p>}
      <button onClick={() => setCreating(!creating)}>{creating ? 'Close' : 'New workflow'}</button>
      {creating && catalog.data && <Builder catalog={catalog.data} onSaved={(id) => { setCreating(false); setOpen(id) }} />}
      {list.data?.length === 0 && <p className="muted">No workflows yet.</p>}
      {list.data?.map((w) => (
        <div className="card" key={w.id}>
          <strong>{w.name}</strong> <span className="muted">· when {label(w.trigger).toLowerCase()} · v{w.currentVersion}</span>{' '}
          <span className={w.enabled ? 'badge ok' : 'badge warn'}>{w.enabled ? 'On' : 'Paused'}</span>{' '}
          <button className="link" onClick={() => setOpen(open === w.id ? '' : w.id)}>{open === w.id ? 'Hide' : 'Details'}</button>
          {open === w.id && catalog.data && <WorkflowDetail id={w.id} catalog={catalog.data} />}
        </div>
      ))}
    </>
  )
}

function WorkflowDetail({ id, catalog }: { id: string; catalog: Catalog }) {
  const qc = useQueryClient()
  const d = useQuery({ queryKey: ['workflow', id], queryFn: () => api.get<Detail>(`/api/v1/automation/workflows/${id}`) })
  const runs = useQuery({ queryKey: ['runs', id], queryFn: () => api.get<RunRow[]>(`/api/v1/automation/workflows/${id}/runs`), refetchInterval: 15_000 })
  const [run, setRun] = useState(''); const [editing, setEditing] = useState(false)
  const toggle = useMutation({
    mutationFn: (on: boolean) => api.post(`/api/v1/automation/workflows/${id}/${on ? 'enable' : 'disable'}`),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['workflows'] }); void qc.invalidateQueries({ queryKey: ['workflow', id] }) },
  })
  if (!d.data) return <p className="muted">Loading…</p>
  const w = d.data.workflow
  return (
    <div>
      <div className="row">
        <button className="secondary" onClick={() => toggle.mutate(!w.enabled)}>{w.enabled ? 'Pause' : 'Turn on'}</button>
        <button className="secondary" onClick={() => setEditing(!editing)}>{editing ? 'Cancel edit' : 'Edit (new version)'}</button>
      </div>
      {err(toggle.error) && <p className="error" role="alert">{err(toggle.error)}</p>}
      {editing ? <Builder catalog={catalog} existing={d.data} onSaved={() => { setEditing(false); void qc.invalidateQueries({ queryKey: ['workflow', id] }); void qc.invalidateQueries({ queryKey: ['workflows'] }) }} /> : (
        <>
          {d.data.definition.conditions.length > 0 && <p>Only when: {d.data.definition.conditions.map((c) => `${c.field} ${c.op} ${c.value ?? ''}`).join(' and ')}</p>}
          <ol>{d.data.definition.steps.map((s, i) => <li key={i}><StepText s={s} /></li>)}</ol>
        </>
      )}
      <h3>Recent runs</h3>
      {runs.data?.length === 0 && <p className="muted">Nothing has run yet.</p>}
      {!!runs.data?.length && (
        <table>
          <thead><tr><th>Started</th><th>Version</th><th>Status</th><th /></tr></thead>
          <tbody>{runs.data.map((r) => (
            <tr key={r.id}><td>{new Date(r.createdAt).toLocaleString()}</td><td>v{r.versionNumber}</td>
              <td><span className={r.status === 'Failed' ? 'badge warn' : 'badge'}>{r.status}</span>{r.error && <span className="muted"> {r.error}</span>}</td>
              <td><button className="link" onClick={() => setRun(run === r.id ? '' : r.id)}>Log</button></td></tr>
          ))}</tbody>
        </table>
      )}
      {run && <RunLog id={run} />}
    </div>
  )
}

function StepText({ s }: { s: Step }) {
  switch (s.kind) {
    case 'wait': return <>Wait {s.minutes} minutes</>
    case 'if': return <>Continue only if {s.field} {s.op} {s.value}</>
    case 'task': return <>Create task “{s.title}” for {s.assignee ?? 'owner'}{s.dueInMinutes != null && `, due in ${s.dueInMinutes} min`}</>
    case 'notify': return <>Notify {s.to ?? 'owner'}: “{s.message}”</>
    case 'webhook': return <>Call n8n webhook “{s.name}”</>
    case 'waituntil': return <>Wait until {Math.abs(s.offsetMinutes ?? 0)} min {(s.offsetMinutes ?? 0) < 0 ? 'before' : 'after'} {s.field}</>
    case 'email': return <>Send a {s.purpose} email “{s.subject}”</>
    default: return <>{s.kind}</>
  }
}

function RunLog({ id }: { id: string }) {
  const q = useQuery({ queryKey: ['run', id], queryFn: () => api.get<RunDetail>(`/api/v1/automation/runs/${id}`) })
  if (!q.data) return <p className="muted">Loading…</p>
  return (
    <div className="card">
      <strong>{q.data.run.workflow}</strong> · {q.data.run.status}{q.data.run.resumeAt && ` · resumes ${new Date(q.data.run.resumeAt).toLocaleString()}`}
      <table><tbody>{q.data.steps.map((s, i) => (
        <tr key={i}><td className="muted">{new Date(s.at).toLocaleString()}</td><td>Step {s.stepIndex + 1} · {s.kind}</td>
          <td><span className={s.status === 'Failed' ? 'badge warn' : s.status === 'Succeeded' ? 'badge ok' : 'badge'}>{s.status}</span></td><td>{s.detail}</td></tr>
      ))}</tbody></table>
    </div>
  )
}

function Builder({ catalog, existing, onSaved }: { catalog: Catalog; existing?: Detail; onSaved: (id: string) => void }) {
  const qc = useQueryClient()
  const products = useQuery({ queryKey: ['products'], queryFn: () => api.get<Product[]>('/api/v1/products') })
  const [name, setName] = useState(existing?.workflow.name ?? ''); const [trigger, setTrigger] = useState(existing?.workflow.trigger ?? 'lead.created')
  const [productId, setProductId] = useState(existing?.workflow.productId ?? '')
  const [conditions, setConditions] = useState<Cond[]>(existing?.definition.conditions ?? [])
  const [steps, setSteps] = useState<Step[]>(existing?.definition.steps ?? [])
  const [note, setNote] = useState(''); const [kind, setKind] = useState('task')
  const fields = catalog.triggers.find((t) => t.type === trigger)?.fields ?? []
  const save = useMutation({
    mutationFn: async () => {
      const definition = { conditions, steps }
      if (existing) { await api.put(`/api/v1/automation/workflows/${existing.workflow.id}`, { name, definition, note: note || null, baseVersion: existing.workflow.currentVersion }); return existing.workflow.id }
      return (await api.post<{ id: string }>('/api/v1/automation/workflows', { name, trigger, productId: productId || null, definition, note: note || null })).id
    },
    onSuccess: (id) => { void qc.invalidateQueries({ queryKey: ['workflows'] }); onSaved(id) },
  })
  const setStep = (i: number, p: Partial<Step>) => setSteps(steps.map((s, j) => (j === i ? { ...s, ...p } : s)))
  const setCond = (i: number, p: Partial<Cond>) => setConditions(conditions.map((c, j) => (j === i ? { ...c, ...p } : c)))
  const opts = (xs: string[]) => xs.map((x) => <option key={x} value={x}>{label(x)}</option>)

  return (
    <div className="card">
      <div className="row">
        <label>Name<input value={name} onChange={(e) => setName(e.target.value)} /></label>
        {!existing && (
          <>
            <label>When this happens<select value={trigger} onChange={(e) => setTrigger(e.target.value)}>{opts(catalog.triggers.map((t) => t.type))}</select></label>
            <label>Product
              <select value={productId} onChange={(e) => setProductId(e.target.value)}>
                <option value="">Every product</option>{products.data?.filter((p) => !p.isArchived).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            </label>
          </>
        )}
      </div>
      <h3>Only if (optional)</h3>
      {conditions.map((c, i) => (
        <div className="row" key={i}>
          <label>Field<select value={c.field} onChange={(e) => setCond(i, { field: e.target.value })}><option value="">Choose…</option>{opts(fields)}</select></label>
          <label>Is<select value={c.op} onChange={(e) => setCond(i, { op: e.target.value })}>{opts(catalog.ops)}</select></label>
          <label>Value<input value={c.value ?? ''} onChange={(e) => setCond(i, { value: e.target.value })} /></label>
          <button className="link" onClick={() => setConditions(conditions.filter((_, j) => j !== i))}>Remove</button>
        </div>
      ))}
      <button className="secondary" onClick={() => setConditions([...conditions, { field: fields[0] ?? '', op: 'eq', value: '' }])}>Add condition</button>

      <h3>Then, in order</h3>
      <p className="muted">Use {'{{contactName}}'}-style placeholders in text. “owner” means the person responsible for the record.</p>
      {steps.map((s, i) => (
        <div className="row" key={i} style={{ borderTop: '1px solid var(--line)', paddingTop: '.5rem' }}>
          <strong>{i + 1}. {label(s.kind)}</strong>
          {s.kind === 'wait' && <label>Minutes<input type="number" min={1} value={s.minutes ?? 60} onChange={(e) => setStep(i, { minutes: Number(e.target.value) })} /></label>}
          {s.kind === 'if' && <>
            <label>Field<select value={s.field} onChange={(e) => setStep(i, { field: e.target.value })}>{opts(fields)}</select></label>
            <label>Is<select value={s.op} onChange={(e) => setStep(i, { op: e.target.value })}>{opts(catalog.ops)}</select></label>
            <label>Value<input value={s.value ?? ''} onChange={(e) => setStep(i, { value: e.target.value })} /></label></>}
          {s.kind === 'task' && <>
            <label>Title<input value={s.title ?? ''} onChange={(e) => setStep(i, { title: e.target.value })} /></label>
            <label>Due in (min)<input type="number" min={0} value={s.dueInMinutes ?? 0} onChange={(e) => setStep(i, { dueInMinutes: Number(e.target.value) })} /></label>
            <label>Assign to<input value={s.assignee ?? 'owner'} onChange={(e) => setStep(i, { assignee: e.target.value })} /></label>
            <span className="muted" style={{ fontSize: '.8rem' }}>“owner” = the record’s owner (a lead from a form has none unless the form has an owner pool, and the step is then skipped). “none” = an unassigned task. Or a user id.</span></>}
          {s.kind === 'notify' && <>
            <label>Message<input value={s.message ?? ''} onChange={(e) => setStep(i, { message: e.target.value })} /></label>
            <label>To<input value={s.to ?? 'owner'} onChange={(e) => setStep(i, { to: e.target.value })} /></label></>}
          {s.kind === 'waituntil' && <>
            <label>Date field<select value={s.field} onChange={(e) => setStep(i, { field: e.target.value })}>{opts(fields)}</select></label>
            <label>Minutes (negative = before)<input type="number" value={s.offsetMinutes ?? 0} onChange={(e) => setStep(i, { offsetMinutes: Number(e.target.value) })} /></label></>}
          {s.kind === 'email' && <>
            <label>Type<select value={s.purpose} onChange={(e) => setStep(i, { purpose: e.target.value })}><option value="transactional">Relationship (confirmations, reminders)</option><option value="marketing">Marketing (needs recorded consent)</option></select></label>
            <label>Subject<input value={s.subject ?? ''} onChange={(e) => setStep(i, { subject: e.target.value })} /></label>
            <label>Message<textarea rows={3} value={s.body ?? ''} onChange={(e) => setStep(i, { body: e.target.value })} /></label></>}
          {s.kind === 'webhook' && <label>Webhook<select value={s.name ?? ''} onChange={(e) => setStep(i, { name: e.target.value })}><option value="">Choose…</option>{opts(catalog.webhooks)}</select></label>}
          <button className="link" onClick={() => setSteps(steps.filter((_, j) => j !== i))}>Remove</button>
        </div>
      ))}
      <div className="row">
        <label>Add a step<select value={kind} onChange={(e) => setKind(e.target.value)}>{opts(catalog.stepKinds)}</select></label>
        <button className="secondary" onClick={() => setSteps([...steps, blankStep(kind)])}>Add step</button>
      </div>
      <div className="row">
        <label>Note about this version<input value={note} onChange={(e) => setNote(e.target.value)} /></label>
        <button disabled={!name.trim() || steps.length === 0 || save.isPending} onClick={() => save.mutate()}>{existing ? 'Save as new version' : 'Create (starts paused)'}</button>
      </div>
      {err(save.error) && <p className="error" role="alert">{err(save.error)}</p>}
    </div>
  )
}
