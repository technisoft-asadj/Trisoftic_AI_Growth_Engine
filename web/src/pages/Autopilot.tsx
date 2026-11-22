import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../api.ts'

const err = (e: unknown) => (e instanceof ApiError ? e.message : e ? 'Something went wrong' : null)

interface Product { id: string; name: string; isArchived: boolean }
interface Check { name: string; passed: boolean; detail: string }
interface Decision { id: string; contentItemId?: string; version: number; verdict: string; attempt: number; reasons: string; costUsd: number; createdAt: string; checks: Check[] }
interface State {
  enabled: boolean; channel: string; language?: string | null; goal: string; planDays: number; everyDays: number; postTimeUtc: string; postTimesUtc?: string[]; maxPostsPerDay: number; lookaheadHours: number; alsoInstagram?: boolean; useImages: boolean; useVideos: boolean
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
  const [f, setF] = useState({ goal: '', channel: 'Facebook', language: '', planDays: 7, everyDays: 1, postTimesUtc: ['13:00'], maxPostsPerDay: 2, lookaheadHours: 72, useImages: true, useVideos: true, alsoInstagram: false })
  const [loaded, setLoaded] = useState(false)
  useEffect(() => {
    if (q.data && !loaded) {
      setLoaded(true)
      setF({ goal: q.data.goal, channel: q.data.channel, language: q.data.language ?? '', planDays: q.data.planDays, everyDays: q.data.everyDays, postTimesUtc: q.data.postTimesUtc?.length ? q.data.postTimesUtc : [q.data.postTimeUtc], maxPostsPerDay: q.data.maxPostsPerDay, lookaheadHours: q.data.lookaheadHours, useImages: q.data.useImages, useVideos: q.data.useVideos, alsoInstagram: q.data.alsoInstagram ?? false })
    }
  }, [q.data, loaded])
  const save = useMutation({
    mutationFn: (enabled: boolean) => api.put(`/api/v1/products/${pid}/autopilot`, { enabled, ...f, language: f.language || null }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['autopilot', pid] }),
  })
  // changing the count keeps the first time and spreads the others: 5 hours apart for three posts, 6 for two
  const setTimes = (count: number) => {
    const first = f.postTimesUtc[0] || '13:00'; const [h, m] = first.split(':').map(Number)
    const at = (plus: number) => `${String((h + plus) % 24).padStart(2, '0')}:${String(m || 0).padStart(2, '0')}`
    setF({ ...f, postTimesUtc: count === 1 ? [first] : count === 2 ? [first, at(6)] : [first, at(5), at(10)], maxPostsPerDay: Math.max(f.maxPostsPerDay, count) })
  }
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
          <label>Posts per day<select value={f.postTimesUtc.length} onChange={(e) => setTimes(Number(e.target.value))}><option value={1}>1</option><option value={2}>2</option><option value={3}>3</option></select></label>
          {f.postTimesUtc.map((t, i) => (
            <label key={i}>Time {f.postTimesUtc.length > 1 ? i + 1 : ''} (UTC)<input type="time" value={t} onChange={(e) => setF({ ...f, postTimesUtc: f.postTimesUtc.map((x, j) => (j === i ? e.target.value : x)) })} /></label>
          ))}
          <label>Post every (days)<input type="number" min={1} max={7} value={f.everyDays} onChange={(e) => setF({ ...f, everyDays: Number(e.target.value) })} /></label>
          <label>Plan length (days)<input type="number" min={3} max={31} value={f.planDays} onChange={(e) => setF({ ...f, planDays: Number(e.target.value) })} /></label>
          <label title="A ceiling, not a target: the number of posts per day comes from Posts per day">Most posts per day (ceiling)<input type="number" min={1} max={5} value={f.maxPostsPerDay} onChange={(e) => setF({ ...f, maxPostsPerDay: Number(e.target.value) })} /></label>
          <label>Write ahead (hours)<input type="number" min={24} max={168} value={f.lookaheadHours} onChange={(e) => setF({ ...f, lookaheadHours: Number(e.target.value) })} /></label>
        </div>
        <label><input type="checkbox" checked={f.useImages} onChange={(e) => setF({ ...f, useImages: e.target.checked })} /> Add pictures to posts when they help: your own uploaded pictures first, then AI cartoon pictures (about $0.04 each, within the monthly limit)</label>
        <label><input type="checkbox" checked={f.useVideos} onChange={(e) => setF({ ...f, useVideos: e.target.checked })} /> Post Reels from your uploaded vertical videos (9:16, 3 to 90 seconds, usage rights set in Assets)</label>
        <label><input type="checkbox" checked={f.alsoInstagram} onChange={(e) => setF({ ...f, alsoInstagram: e.target.checked })} /> Also post to Instagram: each approved post that has a picture or video is copied to your connected Instagram account an hour later, with its own tracked link. Posts without media are skipped (Instagram has no text-only posts). Connect Instagram under Publishing first.</label>
        <p className="muted">The planner chooses text, picture or video for every post, favouring what the results table shows works and otherwise mixing them so we learn. Each picture or video is used once. It never makes video, and AI pictures never show people, app screens or gameplay. If no picture or video is available, the post goes out as text.</p>
        <button type="submit" className="secondary" disabled={save.isPending || !f.goal.trim()}>Save settings</button>
        <p className="muted">It only works from the verified product profile, never publishes more than the daily limit, stops when AI spending reaches its monthly budget, and handles only plans it created itself.</p>
      </form>

      <OwnerOps pid={pid} />

      <LinkClicks pid={pid} />

      <Learnings pid={pid} />

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

interface LearningRow { id: string; statement: string; howToTest?: string | null; status: string; confidence: string; evidence?: string | null; timesTested: number; fromReview: boolean }
const learnClass = (s: string) => (s === 'Supported' ? 'badge ok' : s === 'Contradicted' ? 'badge warn' : 'badge')

/** What we think works, what the results later showed, and how sure we are. Every new plan has to test the open ones. */
function Learnings({ pid }: { pid: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['learnings', pid], queryFn: () => api.get<LearningRow[]>(`/api/v1/products/${pid}/learnings`) })
  const [text, setText] = useState('')
  const reload = () => void qc.invalidateQueries({ queryKey: ['learnings', pid] })
  const add = useMutation({ mutationFn: () => api.post(`/api/v1/products/${pid}/learnings`, { statement: text }), onSuccess: () => { setText(''); reload() } })
  const retire = useMutation({ mutationFn: (id: string) => api.post(`/api/v1/learnings/${id}/retire`), onSuccess: reload })
  const rows = q.data ?? []
  return (
    <div className="card">
      <h3>What we are learning</h3>
      <p className="muted">After each plan (and every week while a long plan runs) the review adds guesses worth testing and judges the open ones from the results. The next plan must test the open ones. A verdict of "supported" or "contradicted" needs at least 5 posts with figures; otherwise it stays open as inconclusive and is retired after 3 tests.</p>
      {rows.length === 0 && <p className="muted">Nothing yet. The first review adds entries here.</p>}
      {rows.map((l) => (
        <p key={l.id}>
          <span className={learnClass(l.status)}>{l.status}</span> <b>{l.statement}</b>
          {l.status !== 'Open' || l.timesTested > 0 ? <span className="muted"> · confidence {l.confidence} · tested {l.timesTested}x</span> : null}
          {l.howToTest && <><br /><span className="muted">How to test: {l.howToTest}</span></>}
          {l.evidence && <><br /><span className="muted">Result: {l.evidence}</span></>}
          {l.status === 'Open' && <> <button className="link" disabled={retire.isPending} onClick={() => { if (window.confirm('Retire this hypothesis? Plans will stop testing it.')) retire.mutate(l.id) }}>Retire</button></>}
        </p>
      ))}
      <form className="row" onSubmit={(e: FormEvent) => { e.preventDefault(); add.mutate() }}>
        <label style={{ flex: 1 }}>Add your own guess<input value={text} maxLength={300} onChange={(e) => setText(e.target.value)} placeholder="For example: posts in the evening get more comments than posts at lunchtime" /></label>
        <button type="submit" className="secondary" disabled={add.isPending || text.trim().length < 5}>Add</button>
      </form>
      {err(add.error ?? retire.error) && <p className="error" role="alert">{err(add.error ?? retire.error)}</p>}
    </div>
  )
}

interface OpsData { alerts: { id: string; key: string; severity: string; message: string; openedAt: string }[]; digests: { id: string; subject: string; body: string; createdAt: string; emailed: boolean }[] }

/** Problems that need the owner, and the weekly report. Alerts close by themselves when the cause is gone. */
function OwnerOps({ pid }: { pid: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['ops', pid], queryFn: () => api.get<OpsData>(`/api/v1/products/${pid}/ops`), refetchInterval: 60000 })
  const reload = () => void qc.invalidateQueries({ queryKey: ['ops', pid] })
  const check = useMutation({ mutationFn: () => api.post(`/api/v1/products/${pid}/ops/check`), onSuccess: reload })
  const digest = useMutation({ mutationFn: () => api.post(`/api/v1/products/${pid}/ops/digest`), onSuccess: reload })
  const alerts = q.data?.alerts ?? []; const digests = q.data?.digests ?? []
  return (
    <div className="card">
      <h3>Problems and weekly report</h3>
      {alerts.length === 0 ? <p className="muted">No open problems.</p> : alerts.map((x) => (
        <p key={x.id}><span className={x.severity === 'critical' ? 'badge warn' : 'badge'}>{x.severity}</span> {x.message} <span className="muted">since {new Date(x.openedAt).toLocaleString()}</span></p>
      ))}
      <div className="row">
        <button className="secondary" disabled={check.isPending} onClick={() => check.mutate()}>Check now</button>
        <button className="secondary" disabled={digest.isPending} onClick={() => digest.mutate()}>Write this week's report now</button>
      </div>
      {digests.length === 0 && <p className="muted">The first weekly report is written a week after the autopilot starts. If an owner email is configured on the server, problems and reports are also emailed.</p>}
      {digests.map((d, i) => (
        <details key={d.id} open={i === 0}>
          <summary><b>{d.subject}</b> <span className="muted">{new Date(d.createdAt).toLocaleDateString()}{d.emailed ? ' · emailed' : ''}</span></summary>
          <pre style={{ whiteSpace: 'pre-wrap', fontFamily: 'inherit' }}>{d.body}</pre>
        </details>
      ))}
      {err(check.error ?? digest.error) && <p className="error" role="alert">{err(check.error ?? digest.error)}</p>}
    </div>
  )
}

interface LinkRowData { id: string; code: string; shortUrl?: string | null; destination: string; label?: string | null; channel: string; contentItemId?: string | null; createdAt: string; clicks: number; unique: number; lastClickAt?: string | null }
interface LinkReport { days: number; totalClicks: number; totalUnique: number; note: string; byPost: { contentItemId: string; clicks: number; unique: number; lastClickAt?: string | null; links: LinkRowData[] }[]; byDay: { day: string; clicks: number }[]; loose: LinkRowData[] }

/** Clicks on the short links in posts, per post. A click is someone opening the link; this is not an install count. */
function LinkClicks({ pid }: { pid: string }) {
  const qc = useQueryClient()
  const [days, setDays] = useState(30)
  const q = useQuery({ queryKey: ['links', pid, days], queryFn: () => api.get<LinkReport>(`/api/v1/products/${pid}/links/report?days=${days}`), refetchInterval: 60000 })
  const titles = useQuery({ queryKey: ['link-titles', pid], queryFn: () => api.get<{ id: string; title: string; status: string }[]>(`/api/v1/content?productId=${pid}&take=200`) })
  const title = (id: string) => titles.data?.find((c) => c.id === id)?.title ?? 'Post'
  const [url, setUrl] = useState(''); const [label, setLabel] = useState(''); const [channel, setChannel] = useState('facebook')
  const make = useMutation({
    mutationFn: () => api.post<{ shortUrl: string }>(`/api/v1/products/${pid}/links`, { url, label: label || null, channel }),
    onSuccess: () => { setUrl(''); setLabel(''); void qc.invalidateQueries({ queryKey: ['links', pid] }) },
  })
  const d = q.data
  return (
    <div className="card">
      <h3>Link clicks</h3>
      <p className="muted">The autopilot swaps the store link in each post for a short link that counts clicks and sends people on to the store page with tracking tags. {d?.note}</p>
      <label>Period <select value={days} onChange={(e) => setDays(Number(e.target.value))}><option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option></select></label>
      {d && <p><b>{d.totalClicks}</b> click(s)<span className="muted"> from about {d.totalUnique} visitor(s) in the last {d.days} days</span></p>}
      {d && d.byPost.length === 0 && <p className="muted">No posts with short links yet. New posts written by the autopilot carry them.</p>}
      {d && d.byPost.length > 0 && (
        <table>
          <thead><tr><th>Post</th><th>Clicks</th><th>Visitors</th><th>Last click</th><th>Link</th></tr></thead>
          <tbody>
            {d.byPost.map((p) => (
              <tr key={p.contentItemId}>
                <td>{title(p.contentItemId)}</td><td>{p.clicks}</td><td>{p.unique}</td>
                <td>{p.lastClickAt ? new Date(p.lastClickAt).toLocaleString() : '-'}</td>
                <td>{p.links[0]?.shortUrl && <a href={p.links[0].shortUrl} target="_blank" rel="noreferrer">{p.links[0].shortUrl.replace('https://', '')}</a>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <h4>Make a link for somewhere else</h4>
      <p className="muted">For a bio, a message, a flyer or a post you write yourself. It counts clicks the same way.</p>
      <form className="row" onSubmit={(e: FormEvent) => { e.preventDefault(); make.mutate() }}>
        <label style={{ flex: 2 }}>Address<input type="url" required value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://play.google.com/store/apps/details?id=..." /></label>
        <label>Where it will be shared<input value={channel} onChange={(e) => setChannel(e.target.value)} /></label>
        <label>Note<input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="Instagram bio" /></label>
        <button type="submit" className="secondary" disabled={make.isPending || url.length < 8}>Make link</button>
      </form>
      {make.data && <p>Your link: <b>{make.data.shortUrl}</b></p>}
      {d && d.loose.length > 0 && (
        <table>
          <thead><tr><th>Note</th><th>Link</th><th>Clicks</th></tr></thead>
          <tbody>{d.loose.map((l) => <tr key={l.id}><td>{l.label || l.channel}</td><td>{l.shortUrl}</td><td>{l.clicks}</td></tr>)}</tbody>
        </table>
      )}
      {err(make.error) && <p className="error" role="alert">{err(make.error)}</p>}
    </div>
  )
}
