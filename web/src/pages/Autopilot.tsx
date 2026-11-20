import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../api.ts'

const err = (e: unknown) => (e instanceof ApiError ? e.message : e ? 'Something went wrong' : null)

interface Product { id: string; name: string; isArchived: boolean }
interface Check { name: string; passed: boolean; detail: string }
interface Decision { id: string; contentItemId?: string; version: number; verdict: string; attempt: number; reasons: string; costUsd: number; createdAt: string; checks: Check[] }
interface State {
  enabled: boolean; channel: string; language?: string | null; goal: string; planDays: number; everyDays: number; postTimeUtc: string; maxPostsPerDay: number; lookaheadHours: number
  lastRunAt?: string | null; lastRunNote?: string | null
  needsYou: { contentItemId: string; reasons: string; createdAt: string; title: string }[]
  week: { verdict: string; n: number; cost: number }[]
  decisions: Decision[]
}

const verdictClass = (v: string) => (v === 'Approved' ? 'badge ok' : v === 'Rejected' || v === 'Escalated' ? 'badge warn' : 'badge')

/** Runs a product's growth loop with nobody pressing anything. The AI reviewer approves what passes every check; anything it is unsure about comes to the "Needs you" list. */
export function AutopilotPage() {
  const products = useQuery({ queryKey: ['products'], queryFn: () => api.get<Product[]>('/api/v1/products') })
  const [chosen, setChosen] = useState('')
  const pid = chosen || products.data?.find((p) => !p.isArchived)?.id || ''
  return (
    <>
      <h2>Autopilot</h2>
      <p className="muted">Switch it on and the platform plans the next days, writes each post, has an AI reviewer approve it, schedules it, publishes it and learns from the results, with no one pressing anything. Replies to comments are not part of this: they still wait for a person.</p>
      <label>Product
        <select value={pid} onChange={(e) => setChosen(e.target.value)}>{products.data?.filter((p) => !p.isArchived).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select>
      </label>
      {pid && <Panel key={pid} pid={pid} />}
    </>
  )
}

function Panel({ pid }: { pid: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['autopilot', pid], queryFn: () => api.get<State>(`/api/v1/products/${pid}/autopilot`), refetchInterval: 30000 })
  const [f, setF] = useState({ goal: '', channel: 'Facebook', language: '', planDays: 7, everyDays: 1, postTimeUtc: '13:00', maxPostsPerDay: 2, lookaheadHours: 72 })
  const [loaded, setLoaded] = useState(false)
  useEffect(() => {
    if (q.data && !loaded) {
      setLoaded(true)
      setF({ goal: q.data.goal, channel: q.data.channel, language: q.data.language ?? '', planDays: q.data.planDays, everyDays: q.data.everyDays, postTimeUtc: q.data.postTimeUtc, maxPostsPerDay: q.data.maxPostsPerDay, lookaheadHours: q.data.lookaheadHours })
    }
  }, [q.data, loaded])
  const save = useMutation({
    mutationFn: (enabled: boolean) => api.put(`/api/v1/products/${pid}/autopilot`, { enabled, ...f, language: f.language || null }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['autopilot', pid] }),
  })
  const run = useMutation({ mutationFn: () => api.post<{ notes: string[] }>(`/api/v1/products/${pid}/autopilot/run`), onSuccess: () => void qc.invalidateQueries({ queryKey: ['autopilot', pid] }) })
  const [open, setOpen] = useState<string | null>(null)
  const d = q.data
  const problem = err(save.error ?? run.error ?? q.error)
  const n = (v: string) => d?.week.find((w) => w.verdict === v)?.n ?? 0

  return (
    <>
      {problem && <p className="error" role="alert">{problem}</p>}
      {d && (
        <div className="card">
          <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12 }}>
            <div>
              <h3 style={{ margin: 0 }}>Autopilot is <span className={d.enabled ? 'badge ok' : 'badge'}>{d.enabled ? 'ON' : 'OFF'}</span></h3>
              <p className="muted" style={{ margin: '4px 0 0' }}>
                {d.lastRunAt ? <>Last ran {new Date(d.lastRunAt).toLocaleString()}. {d.lastRunNote}</> : 'It has not run yet.'}
              </p>
            </div>
            <div className="row" style={{ gap: 8 }}>
              {d.enabled && <button className="secondary" disabled={run.isPending} onClick={() => run.mutate()}>{run.isPending ? 'Running…' : 'Run now'}</button>}
              <button disabled={save.isPending || !f.goal.trim()} className={d.enabled ? 'secondary' : ''}
                onClick={() => { if (d.enabled || window.confirm('Switch the autopilot on?\n\nPosts will be written, approved by the AI reviewer and published on this Page without you seeing them first. You can switch it off here at any time, and the Pause switch in Admin stops everything.')) save.mutate(!d.enabled) }}>
                {d.enabled ? 'Switch off' : 'Switch on'}
              </button>
            </div>
          </div>
          {run.data && <p className="muted">{run.data.notes.join(' ') || 'Nothing needed doing.'}</p>}
          <p className="muted">Last 7 days: {n('Approved')} approved by the AI reviewer, {n('Rejected')} rejected and rewritten, {n('Escalated')} handed to you.</p>
        </div>
      )}

      {!!d?.needsYou.length && (
        <div className="card">
          <h3>Needs you ({d.needsYou.length})</h3>
          <p className="muted">The reviewer was not sure about these, so nothing is published until you decide. Approve or reject them in <Link to="/approvals">Approvals</Link>.</p>
          {d.needsYou.map((x) => <p key={x.contentItemId}><b>{x.title}</b><br /><span className="muted">{x.reasons}</span></p>)}
        </div>
      )}

      <form className="card" onSubmit={(e: FormEvent) => { e.preventDefault(); save.mutate(d?.enabled ?? false) }}>
        <h3>What it works from</h3>
        <label>Goal
          <textarea rows={3} value={f.goal} maxLength={2000} onChange={(e) => setF({ ...f, goal: e.target.value })} placeholder="For example: grow followers and downloads of Ludo Fight among Pakistani Ludo players" />
        </label>
        <div className="row" style={{ flexWrap: 'wrap', gap: 12 }}>
          <label>Channel<input value={f.channel} onChange={(e) => setF({ ...f, channel: e.target.value })} /></label>
          <label>Language<input value={f.language} onChange={(e) => setF({ ...f, language: e.target.value })} placeholder="en" /></label>
          <label>Post time (UTC)<input value={f.postTimeUtc} onChange={(e) => setF({ ...f, postTimeUtc: e.target.value })} /></label>
          <label>Post every (days)<input type="number" min={1} max={7} value={f.everyDays} onChange={(e) => setF({ ...f, everyDays: Number(e.target.value) })} /></label>
          <label>Plan length (days)<input type="number" min={3} max={31} value={f.planDays} onChange={(e) => setF({ ...f, planDays: Number(e.target.value) })} /></label>
          <label>Most posts per day<input type="number" min={1} max={5} value={f.maxPostsPerDay} onChange={(e) => setF({ ...f, maxPostsPerDay: Number(e.target.value) })} /></label>
          <label>Write ahead (hours)<input type="number" min={24} max={168} value={f.lookaheadHours} onChange={(e) => setF({ ...f, lookaheadHours: Number(e.target.value) })} /></label>
        </div>
        <button type="submit" className="secondary" disabled={save.isPending || !f.goal.trim()}>Save settings</button>
        <p className="muted">It only works from the verified product profile, never publishes more than the daily limit, stops when AI spending reaches its monthly budget, and handles only plans it created itself.</p>
      </form>

      <div className="card">
        <h3>What the AI reviewer decided</h3>
        {d && d.decisions.length === 0 && <p className="muted">No decisions yet.</p>}
        {!!d?.decisions.length && (
          <table>
            <thead><tr><th>When</th><th>Decision</th><th>Why</th><th /></tr></thead>
            <tbody>{d.decisions.map((x) => (
              <tr key={x.id}>
                <td>{new Date(x.createdAt).toLocaleString()}</td>
                <td><span className={verdictClass(x.verdict)}>{x.verdict}</span></td>
                <td>{x.reasons}{open === x.id && <ul>{x.checks.map((c, i) => <li key={i}>{c.passed ? '✓' : '✗'} {c.name}{c.passed ? '' : `: ${c.detail}`}</li>)}</ul>}</td>
                <td>{x.checks.length > 0 && <button className="link" onClick={() => setOpen(open === x.id ? null : x.id)}>{open === x.id ? 'Hide checks' : 'Checks'}</button>}</td>
              </tr>
            ))}</tbody>
          </table>
        )}
      </div>
    </>
  )
}
