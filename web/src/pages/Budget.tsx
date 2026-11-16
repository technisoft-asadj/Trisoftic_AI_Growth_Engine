import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type Product } from '../api.ts'
import { useAuth } from '../auth.tsx'

interface Row { id: string; name: string; scope: string; productId?: string; currency: string; hardCap: number; status: string; periodStart: string; periodEnd: string; exposure: number; level: string }
interface Alloc { id: string; label: string; amount: number; recommended: number; approved: number; committed: number; actual: number; remaining: number }
interface Totals { recommended: number; approved: number; committed: number; actual: number; exposure: number; remaining: number; spentPct: number | null }
interface BudgetDto { id: string; scope: string; productId?: string; name: string; currency: string; periodStart: string; periodEnd: string; hardCap: number; risk: string; warnAtPct: number; stopAtPct: number; status: string; totals: Totals; level: string; allocations: Alloc[] }
interface Change { id: string; kind: string; status: string; currentCap: number; proposedCap: number; reason: string; requestedAt: string; decisionNote?: string; allocations: { label: string; amount: number }[] }
interface Detail { budget: BudgetDto; changes: Change[] }
interface SpendRow { id: string; kind: string; amount: number; allocationId?: string; occurredOn: string; note?: string; source?: string; voided: boolean; voidReason?: string }
interface ChannelPlan { channel: string; spend: number; resultsLow?: number; resultsHigh?: number; basis: string }
interface Scenario { name: string; totalSpend: number; channels: ChannelPlan[]; confidence: string; assumptions: string[]; uncertainty: string[] }
interface Plan {
  mode: string; headline: string; remaining: number; currency: string; resultMetric: string; scenarios: Scenario[]; recommendedScenario?: string; warnings: string[]
  evidence: { channel: string; weeksUsed: number; totalSpend: number; totalResults: number; medianCostPerResult?: number; qualified: boolean; note: string }[]
  experiment?: { budget: number; days: number; channels: string[]; hypotheses: string[]; stopGo: string[]; needsInput?: string }
}
interface PlanRow { planId: string; mode: string; createdAt: string; output: Plan }

const err = (e: unknown) => (e instanceof ApiError ? e.message : e ? 'Something went wrong' : null)
const money = (n: number, c: string) => `${c} ${n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
const levelBadge = (l: string) => (l === 'Stop' ? <span className="badge warn">At the limit</span> : l === 'Warning' ? <span className="badge warn">Near the cap</span> : null)

export function BudgetPage() {
  const [openId, setOpenId] = useState<string | null>(null)
  return openId ? <BudgetDetail id={openId} onBack={() => setOpenId(null)} /> : <BudgetList onOpen={setOpenId} />
}

function BudgetList({ onOpen }: { onOpen: (id: string) => void }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const products = useQuery({ queryKey: ['products'], queryFn: () => api.get<Product[]>('/api/v1/products') })
  const list = useQuery({ queryKey: ['budgets'], queryFn: () => api.get<Row[]>('/api/v1/budget/budgets') })
  const [f, setF] = useState({ name: '', productId: '', currency: 'USD', start: '', end: '', cap: '', labels: '' })
  const create = useMutation({
    mutationFn: () => {
      const lines = f.labels.split(',').map((s) => s.trim()).filter(Boolean)
      const cap = Number(f.cap)
      // Split the cap evenly across the named lines; the owner can change the split before launch.
      const each = lines.length ? Math.floor((cap / lines.length) * 100) / 100 : 0
      const allocations = lines.map((label, i) => ({ label, amount: i === lines.length - 1 ? Math.round((cap - each * (lines.length - 1)) * 100) / 100 : each }))
      return api.post<{ id: string }>('/api/v1/budget/budgets', { scope: 'Product', productId: f.productId || products.data?.find((p) => !p.isArchived)?.id, name: f.name, currency: f.currency, periodStart: f.start, periodEnd: f.end, hardCap: cap, allocations })
    },
    onSuccess: (r) => { void qc.invalidateQueries({ queryKey: ['budgets'] }); onOpen(r.id) },
  })
  return (
    <>
      <h1>Budgets</h1>
      <p className="muted">A budget has a hard cap. Money counts in four separate states — recommended, approved, committed and actual — and only committed and actual count against the cap.</p>
      {err(list.error) && <p className="error" role="alert">{err(list.error)}</p>}
      {list.data?.length === 0 && <p className="muted">No budgets yet.</p>}
      {!!list.data?.length && (
        <table>
          <thead><tr><th>Name</th><th>Status</th><th>Period</th><th>Cap</th><th>Committed + actual</th><th /></tr></thead>
          <tbody>{list.data.map((b) => (
            <tr key={b.id}><td><button className="link" onClick={() => onOpen(b.id)}>{b.name}</button></td><td>{b.status}</td><td>{b.periodStart} → {b.periodEnd}</td>
              <td>{money(b.hardCap, b.currency)}</td><td>{money(b.exposure, b.currency)} {levelBadge(b.level)}</td><td /></tr>
          ))}</tbody>
        </table>
      )}
      <AiUsagePanel />
      {can('budget.propose') && (
        <div className="card">
          <h2>New budget (draft)</h2>
          <div className="row">
            <label>Name<input value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} /></label>
            <label>Product<select value={f.productId} onChange={(e) => setF({ ...f, productId: e.target.value })}>{products.data?.filter((p) => !p.isArchived).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
            <label>Currency<input maxLength={3} value={f.currency} onChange={(e) => setF({ ...f, currency: e.target.value.toUpperCase() })} /></label>
          </div>
          <div className="row">
            <label>Starts<input type="date" value={f.start} onChange={(e) => setF({ ...f, start: e.target.value })} /></label>
            <label>Ends<input type="date" value={f.end} onChange={(e) => setF({ ...f, end: e.target.value })} /></label>
            <label>Hard cap<input type="number" min={0} value={f.cap} onChange={(e) => setF({ ...f, cap: e.target.value })} /></label>
            <label>Lines (comma separated, optional)<input placeholder="e.g. Facebook, Email" value={f.labels} onChange={(e) => setF({ ...f, labels: e.target.value })} /></label>
          </div>
          <button disabled={!f.name.trim() || !f.start || !f.end || !Number(f.cap) || create.isPending} onClick={() => create.mutate()}>Create draft</button>
          {err(create.error) && <p className="error" role="alert">{err(create.error)}</p>}
        </div>
      )}
    </>
  )
}

interface AiUsage {
  month: string; totalUsd: number; calls: number
  byModel: { provider: string; model: string; calls: number; inputTokens: number; outputTokens: number; costUsd: number }[]
  byProduct: { productId?: string; calls: number; costUsd: number }[]
  notCharged: { outcome: string; calls: number }[]
}

function AiUsagePanel() {
  const products = useQuery({ queryKey: ['products'], queryFn: () => api.get<Product[]>('/api/v1/products') })
  const q = useQuery({ queryKey: ['ai-usage'], queryFn: () => api.get<AiUsage>('/api/v1/ai/usage/breakdown') })
  const usd = (n: number) => `USD ${n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 4 })}`
  if (!q.data) return err(q.error) ? <p className="error" role="alert">{err(q.error)}</p> : null
  const d = q.data
  return (
    <div className="card">
      <h2>AI usage, {d.month}</h2>
      <p>{usd(d.totalUsd)} across {d.calls} successful calls. Estimated from token counts; failed or blocked calls are not charged.</p>
      {d.calls === 0 ? <p className="muted">No AI calls this month.</p> : (
        <>
          <table><thead><tr><th>Provider</th><th>Model</th><th>Calls</th><th>Tokens in / out</th><th>Cost</th></tr></thead>
            <tbody>{d.byModel.map((m) => <tr key={m.provider + m.model}><td>{m.provider}</td><td>{m.model}</td><td>{m.calls}</td><td>{m.inputTokens.toLocaleString()} / {m.outputTokens.toLocaleString()}</td><td>{usd(m.costUsd)}</td></tr>)}</tbody></table>
          <table><thead><tr><th>Product</th><th>Calls</th><th>Cost</th></tr></thead>
            <tbody>{d.byProduct.map((p) => <tr key={p.productId ?? 'none'}><td>{products.data?.find((x) => x.id === p.productId)?.name ?? 'Not tied to a product'}</td><td>{p.calls}</td><td>{usd(p.costUsd)}</td></tr>)}</tbody></table>
        </>
      )}
      {d.notCharged.length > 0 && <p className="muted">Not charged: {d.notCharged.map((n) => `${n.calls} ${n.outcome}`).join(', ')}.</p>}
    </div>
  )
}

function BudgetDetail({ id, onBack }: { id: string; onBack: () => void }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const d = useQuery({ queryKey: ['budget', id], queryFn: () => api.get<Detail>(`/api/v1/budget/budgets/${id}`) })
  const spend = useQuery({ queryKey: ['budget-spend', id], queryFn: () => api.get<SpendRow[]>(`/api/v1/budget/budgets/${id}/spend`) })
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['budget', id] }); void qc.invalidateQueries({ queryKey: ['budget-spend', id] }); void qc.invalidateQueries({ queryKey: ['budgets'] }); void qc.invalidateQueries({ queryKey: ['budget-plans', id] }) }
  const [reason, setReason] = useState(''); const [newCap, setNewCap] = useState('')
  const [sp, setSp] = useState({ kind: 'Actual', amount: '', allocationId: '', note: '' })
  const change = useMutation({ mutationFn: (b: { kind: string; proposedCap?: number }) => api.post(`/api/v1/budget/budgets/${id}/changes`, { ...b, reason }), onSuccess: () => { setReason(''); setNewCap(''); refresh() } })
  const [realloc, setRealloc] = useState('')
  const reallocate = useMutation({
    mutationFn: () => {
      const allocations = realloc.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
        const i = l.lastIndexOf(':')
        return { label: l.slice(0, i).trim(), amount: Number(l.slice(i + 1)) }
      })
      return api.post(`/api/v1/budget/budgets/${id}/changes`, { kind: 'Reallocation', allocations, reason })
    },
    onSuccess: () => { setReason(''); setRealloc(''); refresh() },
  })
  const withdraw = useMutation({ mutationFn: (cid: string) => api.post(`/api/v1/budget/changes/${cid}/withdraw`), onSuccess: refresh })
  const record = useMutation({
    mutationFn: () => api.post(`/api/v1/budget/budgets/${id}/spend`, { kind: sp.kind, amount: Number(sp.amount), allocationId: sp.allocationId || null, note: sp.note || null }),
    onSuccess: () => { setSp({ ...sp, amount: '', note: '' }); refresh() },
  })
  const voidIt = useMutation({ mutationFn: (v: { id: string; reason: string }) => api.post(`/api/v1/budget/spend/${v.id}/void`, { reason: v.reason }), onSuccess: refresh })

  if (d.isLoading) return <p className="muted">Loading…</p>
  if (!d.data) return <><button className="link" onClick={onBack}>← Budgets</button><p className="error">{err(d.error) ?? 'Not found'}</p></>
  const { budget: b, changes } = d.data
  const c = b.currency
  const pending = changes.find((x) => x.status === 'Pending')
  const label = (aid?: string) => b.allocations.find((a) => a.id === aid)?.label ?? 'Whole budget'
  const canPropose = can('budget.propose')

  return (
    <>
      <button className="link" onClick={onBack}>← Budgets</button>
      <h1>{b.name} <span className="badge">{b.status}</span> {levelBadge(b.level)}</h1>
      <p className="muted">{b.periodStart} → {b.periodEnd} · hard cap {money(b.hardCap, c)} · warns at {b.warnAtPct}%, stops at {b.stopAtPct}% · risk {b.risk}</p>

      <div className="grid">
        <div className="card tile"><span className="muted">Committed + actual (counts against the cap)</span><strong style={{ fontSize: '1.4rem' }}>{money(b.totals.exposure, c)}</strong><span className="muted">{b.totals.spentPct == null ? '' : `${b.totals.spentPct}% of cap`} · {money(b.totals.remaining, c)} left</span></div>
        <div className="card tile"><span className="muted">Recommended (a suggestion, costs nothing)</span><strong>{money(b.totals.recommended, c)}</strong></div>
        <div className="card tile"><span className="muted">Approved</span><strong>{money(b.totals.approved, c)}</strong></div>
        <div className="card tile"><span className="muted">Committed</span><strong>{money(b.totals.committed, c)}</strong></div>
        <div className="card tile"><span className="muted">Actual</span><strong>{money(b.totals.actual, c)}</strong></div>
      </div>

      <div className="card">
        <h2>Lines</h2>
        {b.allocations.length === 0 ? <p className="muted">No lines: spend is tracked against the whole budget.</p> : (
          <table><thead><tr><th>Line</th><th>Planned</th><th>Recommended</th><th>Approved</th><th>Committed</th><th>Actual</th><th>Left</th></tr></thead>
            <tbody>{b.allocations.map((a) => <tr key={a.id}><td>{a.label}</td><td>{money(a.amount, c)}</td><td>{a.recommended}</td><td>{a.approved}</td><td>{a.committed}</td><td>{a.actual}</td><td>{money(a.remaining, c)}</td></tr>)}</tbody></table>
        )}
      </div>

      <div className="card">
        <h2>Changes needing approval</h2>
        {changes.length === 0 && <p className="muted">None yet.</p>}
        {changes.map((x) => (
          <p key={x.id}>
            <strong>{x.kind}</strong> · <span className="badge">{x.status}</span> · cap {x.currentCap} → {x.proposedCap} · {x.reason}
            {x.decisionNote && <span className="muted"> · “{x.decisionNote}”</span>}
            {x.status === 'Pending' && canPropose && <> <button className="link" onClick={() => withdraw.mutate(x.id)}>Withdraw</button></>}
          </p>
        ))}
        {canPropose && !pending && b.status !== 'Closed' && (
          <div className="row">
            <label>Why<input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Reason for the approver" /></label>
            {b.status === 'Draft' && <button disabled={reason.trim().length < 3 || change.isPending} onClick={() => change.mutate({ kind: 'Launch' })}>Request launch</button>}
            {b.status === 'Approved' && (
              <>
                <label>New cap<input type="number" value={newCap} onChange={(e) => setNewCap(e.target.value)} /></label>
                <button disabled={reason.trim().length < 3 || !Number(newCap) || change.isPending} onClick={() => change.mutate({ kind: 'Increase', proposedCap: Number(newCap) })}>Request cap increase</button>
              </>
            )}
          </div>
        )}
        {canPropose && !pending && b.status === 'Approved' && (
          <div className="row">
            <label>Reallocate (one “Line: amount” per row; must add up within the cap)
              <textarea rows={4} value={realloc} placeholder={b.allocations.map((a) => `${a.label}: ${a.amount}`).join('\n')} onChange={(e) => setRealloc(e.target.value)} /></label>
            <button disabled={reason.trim().length < 3 || !realloc.includes(':') || reallocate.isPending} onClick={() => reallocate.mutate()}>Request reallocation</button>
          </div>
        )}
        {(err(change.error) || err(withdraw.error) || err(reallocate.error)) && <p className="error" role="alert">{err(change.error) ?? err(withdraw.error) ?? err(reallocate.error)}</p>}
      </div>

      <div className="card">
        <h2>Spend</h2>
        {canPropose && (
          <div className="row">
            <label>State<select value={sp.kind} onChange={(e) => setSp({ ...sp, kind: e.target.value })}>{['Recommended', 'Approved', 'Committed', 'Actual'].map((k) => <option key={k}>{k}</option>)}</select></label>
            <label>Amount<input type="number" min={0} value={sp.amount} onChange={(e) => setSp({ ...sp, amount: e.target.value })} /></label>
            <label>Line<select value={sp.allocationId} onChange={(e) => setSp({ ...sp, allocationId: e.target.value })}><option value="">Whole budget</option>{b.allocations.map((a) => <option key={a.id} value={a.id}>{a.label}</option>)}</select></label>
            <label>Note<input value={sp.note} onChange={(e) => setSp({ ...sp, note: e.target.value })} /></label>
            <button disabled={!Number(sp.amount) || record.isPending} onClick={() => record.mutate()}>Record</button>
          </div>
        )}
        {err(record.error) && <p className="error" role="alert">{err(record.error)}</p>}
        {spend.data?.length === 0 && <p className="muted">No spend recorded.</p>}
        {!!spend.data?.length && (
          <table><thead><tr><th>Date</th><th>State</th><th>Amount</th><th>Line</th><th>Note</th><th /></tr></thead>
            <tbody>{spend.data.map((s) => (
              <tr key={s.id} style={s.voided ? { opacity: 0.55 } : undefined}>
                <td>{s.occurredOn}</td><td>{s.kind}{s.source === 'planner' && ' (planner)'}</td><td>{money(s.amount, c)}</td><td>{label(s.allocationId)}</td>
                <td>{s.voided ? <em>Voided: {s.voidReason}</em> : s.note}</td>
                <td>{!s.voided && canPropose && <button className="link" onClick={() => { const r = window.prompt('Why is this entry being voided?'); if (r?.trim()) voidIt.mutate({ id: s.id, reason: r }) }}>Void</button>}</td>
              </tr>
            ))}</tbody></table>
        )}
        {err(voidIt.error) && <p className="error" role="alert">{err(voidIt.error)}</p>}
      </div>

      {b.productId && canPropose && <PlannerPanel id={id} currency={c} onChanged={refresh} />}
    </>
  )
}

function PlannerPanel({ id, currency, onChanged }: { id: string; currency: string; onChanged: () => void }) {
  const qc = useQueryClient()
  const [metric, setMetric] = useState('leads')
  const plans = useQuery({ queryKey: ['budget-plans', id], queryFn: () => api.get<PlanRow[]>(`/api/v1/budget/budgets/${id}/plans`) })
  const gen = useMutation({ mutationFn: () => api.post(`/api/v1/budget/budgets/${id}/plan`, { resultMetric: metric }), onSuccess: () => void qc.invalidateQueries({ queryKey: ['budget-plans', id] }) })
  const adopt = useMutation({ mutationFn: (v: { planId: string; scenario: string }) => api.post(`/api/v1/budget/plans/${v.planId}/adopt`, { scenario: v.scenario }), onSuccess: onChanged })
  const latest = plans.data?.[0]
  const p = latest?.output
  return (
    <div className="card">
      <h2>Planner</h2>
      <p className="muted">Built only from results recorded for this product. With too little history it proposes a small learning experiment instead of a forecast.</p>
      <div className="row">
        <label>Result to plan for<input value={metric} onChange={(e) => setMetric(e.target.value)} /></label>
        <button disabled={!metric.trim() || gen.isPending} onClick={() => gen.mutate()}>Generate plan</button>
      </div>
      {(err(gen.error) || err(adopt.error)) && <p className="error" role="alert">{err(gen.error) ?? err(adopt.error)}</p>}
      {p && latest && (
        <>
          <h3>{p.headline}</h3>
          {p.warnings.length > 0 && <ul className="muted">{p.warnings.map((w) => <li key={w}>{w}</li>)}</ul>}
          {p.mode === 'Forecast' ? p.scenarios.map((s) => (
            <div className="card" key={s.name}>
              <strong>{s.name}</strong> {p.recommendedScenario === s.name && <span className="badge">Suggested</span>} · spend {money(s.totalSpend, currency)} · confidence {s.confidence}
              <table><tbody>{s.channels.map((ch) => <tr key={ch.channel}><td>{ch.channel}</td><td>{money(ch.spend, currency)}</td><td>{ch.resultsLow != null && ch.resultsHigh != null ? `${ch.resultsLow}–${ch.resultsHigh} ${p.resultMetric}` : '—'}</td><td className="muted">{ch.basis}</td></tr>)}</tbody></table>
              {s.uncertainty.length > 0 && <ul className="muted">{s.uncertainty.map((u) => <li key={u}>{u}</li>)}</ul>}
              <button className="secondary" disabled={adopt.isPending} onClick={() => adopt.mutate({ planId: latest.planId, scenario: s.name })}>Record as recommended figures</button>
            </div>
          )) : p.experiment && (
            <div>
              <p>Learning experiment: {money(p.experiment.budget, currency)} over {p.experiment.days} days across {p.experiment.channels.join(', ') || 'channels to be chosen'}.</p>
              {p.experiment.needsInput && <p className="muted">{p.experiment.needsInput}</p>}
              <strong>What we want to learn</strong><ul>{p.experiment.hypotheses.map((h) => <li key={h}>{h}</li>)}</ul>
              <strong>Stop / go rules</strong><ul>{p.experiment.stopGo.map((h) => <li key={h}>{h}</li>)}</ul>
            </div>
          )}
        </>
      )}
    </div>
  )
}
