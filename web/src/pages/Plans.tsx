import { useState, type FormEvent } from 'react'
import { Link, Route, Routes, useNavigate, useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../api.ts'
import { useAuth } from '../auth.tsx'

interface PlanSummary { id: string; name: string; goal: string; channel: string; startDate: string; days: number; status: string; slots: number; drafted: number }
interface Results { publishedAt: string; url?: string; measuredAt?: string; reactions: number | null; comments: number | null; shares: number | null; views: number | null; reach: number | null; clicks: number | null; note?: string }
interface Slot {
  id: string; position: number; scheduledFor: string; angle: string; brief: string; hookType?: string | null; testId?: string | null; variant?: string | null; visual?: string | null; imageBrief?: string | null; status: string; contentItemId?: string
  content?: { title: string; status: string; scheduledFor?: string; approved: boolean } | null; results?: Results | null
}
interface ReviewResult { headline: string; confidence: string; whatWorked: string[]; whatDidNot: string[]; learnings: string[]; nextPlan: { goal: string; suggestedAngles: { angle: string; why: string }[] }; caveats: string[] }
interface Review { id: string; createdAt: string; provider: string; model: string; costUsd: number; dataQuality: { slots: number; published: number; measured: number; withViews: number }; result: ReviewResult }
interface PlanDetail {
  id: string; productId: string; name: string; goal: string; channel: string; language?: string; startDate: string; days: number; everyDays: number; postTimeUtc: string; status: string; rationale: string
  measures: { name: string; why: string }[]; previousPlanId?: string; provider: string; model: string; costUsd: number; slots: Slot[]; reviews: Review[]
}

const dash = (n: number | null | undefined) => (n === null || n === undefined ? '—' : n.toLocaleString())
const local = (iso: string) => new Date(iso).toLocaleString([], { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
const statusClass = (s: string) => (['Approved', 'Scheduled', 'Published', 'Active', 'Drafted'].includes(s) ? 'badge ok' : ['Rejected', 'Cancelled', 'PublishFailed'].includes(s) ? 'badge warn' : 'badge')
const confidenceClass = (c: string) => (c === 'high' ? 'badge ok' : c === 'medium' ? 'badge' : 'badge warn')

function Err({ e }: { e: unknown }) {
  if (!e) return null
  if (!(e instanceof ApiError)) return <div className="error" role="alert">Something went wrong.</div>
  return <div className="error" role="alert">{e.message}{e.violations.length > 0 && <ul>{e.violations.map((v) => <li key={v}>{v}</li>)}</ul>}</div>
}

export function PlansTab({ productId }: { productId: string }) {
  return (
    <Routes>
      <Route index element={<PlanList productId={productId} />} />
      <Route path=":planId" element={<PlanPage productId={productId} />} />
    </Routes>
  )
}

const ymd = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const presets = [
  { key: 'three', label: '3 days — a post a day', days: 3, every: 1 },
  { key: 'week', label: '1 week — a post a day', days: 7, every: 1 },
  { key: 'week3', label: '1 week — three posts', days: 7, every: 2 },
  { key: 'month', label: '1 month — a post every 2 days', days: 30, every: 2 },
]

/** The picked local time on the picked day, as the UTC date and time the server stores. */
function toUtc(date: string, time: string) {
  const d = new Date(`${date}T${time}:00`)
  return { startDate: `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`, timeUtc: `${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')}` }
}

function PlanList({ productId }: { productId: string }) {
  const { can } = useAuth()
  const nav = useNavigate()
  const qc = useQueryClient()
  const list = useQuery({ queryKey: ['plans', productId], queryFn: () => api.get<PlanSummary[]>(`/api/v1/products/${productId}/plans`) })
  const tomorrow = new Date(); tomorrow.setDate(tomorrow.getDate() + 1)
  const [f, setF] = useState({ name: '', goal: '', preset: 'week', start: ymd(tomorrow), time: '18:00', channel: 'Facebook', language: 'en' })
  const create = useMutation({
    mutationFn: () => {
      const p = presets.find((x) => x.key === f.preset)!
      const u = toUtc(f.start, f.time)
      return api.post<{ id: string }>(`/api/v1/products/${productId}/plans`, { name: f.name, goal: f.goal, days: p.days, everyDays: p.every, startDate: u.startDate, timeUtc: u.timeUtc, channel: f.channel, language: f.language || null })
    },
    onSuccess: (r) => { void qc.invalidateQueries({ queryKey: ['plans'] }); nav(r.id) },
  })
  const canCreate = can('campaigns.manage') && can('ai.generate')

  return (
    <>
      <p className="muted">A growth plan decides what to post and when, drafts every post from your approved product profile, and afterwards reviews how the posts did to plan the next round. Nothing is published without your approval of each post.</p>
      {list.data?.length === 0 && <p className="muted">No plans yet.</p>}
      {!!list.data?.length && (
        <table>
          <thead><tr><th>Plan</th><th>Starts</th><th>Length</th><th>Posts drafted</th><th>Status</th></tr></thead>
          <tbody>{list.data.map((p) => (
            <tr key={p.id}><td><Link to={p.id}>{p.name}</Link><div className="muted">{p.goal.slice(0, 90)}</div></td><td>{p.startDate}</td><td>{p.days} days</td><td>{p.drafted} of {p.slots}</td><td><span className={statusClass(p.status)}>{p.status}</span></td></tr>
          ))}</tbody>
        </table>
      )}
      {canCreate && (
        <form className="card" onSubmit={(e: FormEvent) => { e.preventDefault(); create.mutate() }}>
          <h2>New plan</h2>
          <div className="row">
            <label>Name<input value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} required maxLength={200} placeholder="Launch week" /></label>
            <label>Length and pace<select value={f.preset} onChange={(e) => setF({ ...f, preset: e.target.value })}>{presets.map((p) => <option key={p.key} value={p.key}>{p.label}</option>)}</select></label>
          </div>
          <label>Goal<textarea rows={3} value={f.goal} onChange={(e) => setF({ ...f, goal: e.target.value })} required maxLength={2000} placeholder="Introduce Ludo Fight to new players and get them to the Google Play page" /></label>
          <div className="row">
            <label>First post on<input type="date" value={f.start} min={ymd(new Date())} onChange={(e) => setF({ ...f, start: e.target.value })} required /></label>
            <label>At (your time)<input type="time" value={f.time} onChange={(e) => setF({ ...f, time: e.target.value })} required /></label>
            <label>Channel<input value={f.channel} onChange={(e) => setF({ ...f, channel: e.target.value })} required /></label>
            <label>Language<input value={f.language} onChange={(e) => setF({ ...f, language: e.target.value })} placeholder="en" /></label>
          </div>
          <Err e={create.error} />
          <button type="submit" disabled={create.isPending}>{create.isPending ? 'Planning… (can take a minute)' : 'Create plan'}</button>
        </form>
      )}
    </>
  )
}

function PlanPage({ productId }: { productId: string }) {
  const { planId } = useParams()
  const { can } = useAuth()
  const nav = useNavigate()
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['plan', planId], queryFn: () => api.get<PlanDetail>(`/api/v1/plans/${planId}`) })
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['plan', planId] }); void qc.invalidateQueries({ queryKey: ['plans'] }); void qc.invalidateQueries({ queryKey: ['content'] }) }
  const [progress, setProgress] = useState<string | null>(null)
  const [draftError, setDraftError] = useState<unknown>(null)

  const draftAll = useMutation({
    mutationFn: async () => {
      const todo = (q.data?.slots ?? []).filter((s) => s.status === 'Planned')
      setDraftError(null)
      for (const [i, s] of todo.entries()) {
        setProgress(`Writing post ${s.position} (${i + 1} of ${todo.length})…`)
        try { await api.post(`/api/v1/plans/${planId}/slots/${s.id}/draft`, {}) } catch (e) { setDraftError(e); break }
        refresh()
      }
      setProgress(null)
    },
    onSettled: refresh,
  })
  const schedule = useMutation({ mutationFn: () => api.post<{ scheduled: number; skipped: { position: number; reason: string }[] }>(`/api/v1/plans/${planId}/schedule`, {}), onSuccess: refresh })
  const review = useMutation({ mutationFn: () => api.post(`/api/v1/plans/${planId}/review`, {}), onSuccess: refresh })
  const setStatus = useMutation({ mutationFn: (s: string) => api.post(`/api/v1/plans/${planId}/status/${s}`, {}), onSuccess: refresh })
  const next = useMutation({
    mutationFn: (reviewId: string) => {
      const p = q.data!
      const end = new Date(`${p.startDate}T00:00:00Z`); end.setUTCDate(end.getUTCDate() + p.days)
      return api.post<{ id: string }>(`/api/v1/products/${productId}/plans`, {
        name: `${p.name} — next`, goal: p.reviews.find((r) => r.id === reviewId)?.result.nextPlan.goal || p.goal, days: p.days, everyDays: p.everyDays,
        startDate: end.toISOString().slice(0, 10), timeUtc: p.postTimeUtc, channel: p.channel, language: p.language ?? null, fromReviewId: reviewId,
      })
    },
    onSuccess: (r) => { void qc.invalidateQueries({ queryKey: ['plans'] }); nav(`../${r.id}`) },
  })

  if (q.isLoading) return <p className="muted">Loading…</p>
  const p = q.data
  if (!p) return <p>Plan not found.</p>
  const planned = p.slots.filter((s) => s.status === 'Planned').length
  const waiting = p.slots.filter((s) => s.content?.status === 'InReview').length
  const approved = p.slots.filter((s) => s.content?.status === 'Approved').length
  const published = p.slots.filter((s) => s.content?.status === 'Published').length
  const open = p.status === 'Draft' || p.status === 'Active'
  const canManage = can('campaigns.manage')

  return (
    <>
      <p><Link to="..">← All plans</Link></p>
      <h2>{p.name} <span className={statusClass(p.status)}>{p.status}</span></h2>
      <p>{p.goal}</p>
      <p className="muted">{p.channel} · {p.days} days from {p.startDate} · {p.slots.length} posts at {p.postTimeUtc} UTC{p.previousPlanId && <> · built from <Link to={`../${p.previousPlanId}`}>the previous plan’s review</Link></>}</p>

      <div className="card">
        <h3>Why this plan</h3>
        <p>{p.rationale}</p>
        <strong>How we’ll judge it</strong>
        <ul>{p.measures.map((m) => <li key={m.name}><b>{m.name}</b> — {m.why}</li>)}</ul>
        <p className="muted">Planned by {p.provider}/{p.model} · ${p.costUsd.toFixed(4)}. The AI only plans around verified facts in the approved product profile.</p>
      </div>

      <div className="card">
        <h3>Posts</h3>
        <table>
          <thead><tr><th>#</th><th>When (your time)</th><th>Angle</th><th>Post</th><th>Reactions</th><th>Comments</th><th>Shares</th><th>Views</th><th>Clicks</th></tr></thead>
          <tbody>{p.slots.map((s) => (
            <tr key={s.id}>
              <td>{s.position}</td><td>{local(s.scheduledFor)}</td>
              <td><b>{s.angle}</b> {s.hookType && <span className="badge">{s.hookType}</span>} {s.visual && s.visual !== 'text' && <span className="badge">{s.visual === 'video' ? 'Reel' : 'Picture'}</span>}<div className="muted">{s.brief}</div></td>
              <td>{s.content ? <><span className={statusClass(s.content.status)}>{s.content.status}</span> <Link to={`/products/${p.productId}/content`}>open</Link>{s.results?.url && <> · <a href={s.results.url} target="_blank" rel="noreferrer">on {p.channel}</a></>}</> : <span className={statusClass(s.status)}>{s.status === 'Planned' ? 'Not written yet' : s.status}</span>}</td>
              <td>{dash(s.results?.reactions)}</td><td>{dash(s.results?.comments)}</td><td>{dash(s.results?.shares)}</td><td>{dash(s.results?.views)}</td><td>{dash(s.results?.clicks)}</td>
            </tr>
          ))}</tbody>
        </table>
        <p className="muted">A dash means the network has not reported that figure (it is not zero). Figures refresh about every 3 hours and Facebook may take a day to report views and clicks.</p>

        {open && canManage && (
          <p>
            {planned > 0 && <button onClick={() => draftAll.mutate()} disabled={draftAll.isPending}>{draftAll.isPending ? progress ?? 'Writing…' : `Write the ${planned} remaining post${planned > 1 ? 's' : ''}`}</button>}{' '}
            {(approved > 0) && can('publishing.manage') && <button onClick={() => schedule.mutate()} disabled={schedule.isPending}>Schedule the {approved} approved post{approved > 1 ? 's' : ''} at their times</button>}
          </p>
        )}
        {waiting > 0 && <p className="muted">{waiting} post{waiting > 1 ? 's are' : ' is'} waiting for approval in <Link to="/approvals">Approvals</Link>. Approve the ones you like, then come back and schedule them.</p>}
        <Err e={draftError} /><Err e={schedule.error} />
        {schedule.data && <p>{schedule.data.scheduled} scheduled.{schedule.data.skipped.length > 0 && <> Left alone: {schedule.data.skipped.map((x) => `post ${x.position} (${x.reason})`).join('; ')}.</>}</p>}
      </div>

      <HookTests planId={p.id} productId={p.productId} canManage={canManage && open} onChanged={refresh} />

      <div className="card">
        <h3>Results and what to do next</h3>
        {published === 0 ? <p className="muted">Nothing from this plan is published yet, so there is nothing to review.</p> : <p>{published} of {p.slots.length} posts are published.</p>}
        {canManage && can('ai.generate') && <button onClick={() => review.mutate()} disabled={review.isPending}>{review.isPending ? 'Reviewing… (can take a minute)' : 'Review the results'}</button>}
        <Err e={review.error} /><Err e={next.error} />
        {p.reviews.map((r) => (
          <div key={r.id} className="card">
            <h4>{r.result.headline} <span className={confidenceClass(r.result.confidence)}>{r.result.confidence} confidence</span></h4>
            <p className="muted">{new Date(r.createdAt).toLocaleString()} · {r.dataQuality.published} published, {r.dataQuality.measured} with engagement figures, {r.dataQuality.withViews} with views{r.provider !== 'none' && <> · {r.provider}/{r.model} · ${r.costUsd.toFixed(4)}</>}</p>
            {r.result.whatWorked.length > 0 && <><strong>What worked</strong><ul>{r.result.whatWorked.map((x, i) => <li key={i}>{x}</li>)}</ul></>}
            {r.result.whatDidNot.length > 0 && <><strong>What did not</strong><ul>{r.result.whatDidNot.map((x, i) => <li key={i}>{x}</li>)}</ul></>}
            {r.result.learnings.length > 0 && <><strong>Learnings</strong><ul>{r.result.learnings.map((x, i) => <li key={i}>{x}</li>)}</ul></>}
            {r.result.caveats.length > 0 && <><strong>Caveats</strong><ul>{r.result.caveats.map((x, i) => <li key={i}>{x}</li>)}</ul></>}
            {r.result.nextPlan.suggestedAngles.length > 0 && (
              <>
                <strong>Suggested next plan{r.result.nextPlan.goal && <>: {r.result.nextPlan.goal}</>}</strong>
                <ul>{r.result.nextPlan.suggestedAngles.map((a, i) => <li key={i}><b>{a.angle}</b> — {a.why}</li>)}</ul>
                {canManage && can('ai.generate') && <button onClick={() => next.mutate(r.id)} disabled={next.isPending}>{next.isPending ? 'Planning… (can take a minute)' : 'Plan the next round from this review'}</button>}
              </>
            )}
          </div>
        ))}
      </div>

      {open && canManage && <p><button className="link" onClick={() => setStatus.mutate('Completed')}>Mark as completed</button> · <button className="link" onClick={() => { if (confirm('Cancel this plan? Posts already written stay in Content; cancel them there if you do not want them.')) setStatus.mutate('Cancelled') }}>Cancel plan</button></p>}
      <Err e={setStatus.error} />
    </>
  )
}

interface VariantRow { label: string; hookType: string; planned: number; published: number; measured: number; avgEngagements: number | null; avgViews: number | null }
interface TestRow { id: string; planId: string; name: string; status: string; postsPerVariant: number; minMeasured: number; variants: VariantRow[]; verdict: { status: string; winner: string | null; summary: string } }

/** Hook tests: the same message opened in different ways, posted in turn on different days. A winner is only named with enough measured posts and a clear gap. */
function HookTests({ planId, productId, canManage, onChanged }: { planId: string; productId: string; canManage: boolean; onChanged: () => void }) {
  const qc = useQueryClient()
  const tests = useQuery({ queryKey: ['hook-tests', productId], queryFn: () => api.get<TestRow[]>(`/api/v1/products/${productId}/hook-tests`) })
  const hooks = useQuery({ queryKey: ['hook-types'], queryFn: () => api.get<string[]>('/api/v1/hook-types') })
  const [name, setName] = useState('')
  const [brief, setBrief] = useState('')
  const [chosen, setChosen] = useState<string[]>([])
  const [per, setPer] = useState(3)
  const create = useMutation({
    mutationFn: () => api.post(`/api/v1/plans/${planId}/hook-tests`, { name, brief, hookTypes: chosen, postsPerVariant: per }),
    onSuccess: () => { setName(''); setBrief(''); setChosen([]); void qc.invalidateQueries({ queryKey: ['hook-tests', productId] }); onChanged() },
  })
  const conclude = useMutation({ mutationFn: () => api.post(`/api/v1/products/${productId}/hook-tests/conclude`), onSuccess: () => void qc.invalidateQueries({ queryKey: ['hook-tests', productId] }) })
  const toggle = (h: string) => setChosen(chosen.includes(h) ? chosen.filter((x) => x !== h) : chosen.length < 3 ? [...chosen, h] : chosen)
  const mine = (tests.data ?? []).filter((t) => t.planId === planId)
  const dash = (v: number | null) => (v === null ? '—' : String(v))
  return (
    <div className="card">
      <h3>Hook tests</h3>
      <p className="muted">Post the same message opened in 2 or 3 different ways, taking turns on different days, and see which opening gets more engagement. The same message is never posted twice on one day. With small numbers, "no clear winner" is a normal answer.</p>
      {mine.map((t) => (
        <div key={t.id} className="card">
          <b>{t.name}</b> <span className={t.status === 'Concluded' ? 'badge ok' : 'badge'}>{t.status}</span>
          <table>
            <thead><tr><th>Variant</th><th>Opens with</th><th>Posts</th><th>Published</th><th>Measured</th><th>Avg engagements</th><th>Avg views</th></tr></thead>
            <tbody>{t.variants.map((v) => (
              <tr key={v.label}><td>{v.label}{t.verdict.winner === v.label && <> <span className="badge ok">winner</span></>}</td><td>{v.hookType}</td><td>{v.planned}</td><td>{v.published}</td><td>{v.measured} of {t.minMeasured}+</td><td>{dash(v.avgEngagements)}</td><td>{dash(v.avgViews)}</td></tr>
            ))}</tbody>
          </table>
          <p>{t.verdict.summary}</p>
        </div>
      ))}
      {mine.some((t) => t.status === 'Running') && canManage && <button className="secondary" onClick={() => conclude.mutate()} disabled={conclude.isPending}>Record finished tests</button>}
      {canManage && (
        <form onSubmit={(e: FormEvent) => { e.preventDefault(); create.mutate() }}>
          <label>Test name<input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} placeholder="Offline opening" /></label>
          <label>Message to test<textarea rows={2} value={brief} onChange={(e) => setBrief(e.target.value)} maxLength={2000} placeholder="Say that the game can be played against the computer when friends are not around." /></label>
          <div className="row" style={{ flexWrap: 'wrap', gap: 12 }}>
            {hooks.data?.map((h) => <label key={h}><input type="checkbox" checked={chosen.includes(h)} onChange={() => toggle(h)} /> {h}</label>)}
          </div>
          <label>Posts per variant<input type="number" min={3} max={5} value={per} onChange={(e) => setPer(Number(e.target.value))} /></label>
          <button type="submit" disabled={create.isPending || chosen.length < 2 || !name.trim() || !brief.trim()}>Add test ({chosen.length * per} posts on {chosen.length * per} different days)</button>
          <Err e={create.error ?? conclude.error} />
        </form>
      )}
    </div>
  )
}
