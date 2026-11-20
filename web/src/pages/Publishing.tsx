import { useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type Product } from '../api.ts'

interface Account { id: string; provider: string; channel: string; displayName: string; status: string; lastError?: string; lastCheckedAt?: string }
interface Caps { facebookConfigured: boolean; sandboxEnabled: boolean }
interface Failure { itemId: string; productId: string; channel: string; title: string; version: number; reason?: string; updatedAt: string; attempts: { number: number; status: string; error?: string; startedAt: string }[] }
interface Post { contentItemId: string; version: number; providerPostId: string; url?: string; source: string; note?: string; publishedAt: string }

const err = (e: unknown) => (e instanceof ApiError ? e.message : e ? 'Something went wrong' : null)

export function PublishingPage() {
  const [tab, setTab] = useState<'failed' | 'channels' | 'posts' | 'comments'>('failed')
  return (
    <>
      <h1>Publishing</h1>
      <p className="muted">Approved posts go out at their scheduled time. A post is only marked Published once the network confirms it.</p>
      <div className="tabs">
        {(['failed', 'channels', 'posts', 'comments'] as const).map((t) => <button key={t} className={t === tab ? 'active link' : 'link'} onClick={() => setTab(t)}>{{ failed: 'Needs attention', channels: 'Channels', posts: 'Published', comments: 'Comments' }[t]}</button>)}
      </div>
      {tab === 'failed' && <Failures />}
      {tab === 'channels' && <Channels />}
      {tab === 'posts' && <Posts />}
      {tab === 'comments' && <Comments />}
    </>
  )
}

function Failures() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['pub-failures'], queryFn: () => api.get<Failure[]>('/api/v1/publishing/failures'), refetchInterval: 30_000 })
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['pub-failures'] }); void qc.invalidateQueries({ queryKey: ['calendar'] }) }
  const retry = useMutation({ mutationFn: (f: Failure) => api.post(`/api/v1/publishing/content/${f.itemId}/retry`, { version: f.version }), onSuccess: refresh })
  const abandon = useMutation({ mutationFn: (f: Failure) => api.post(`/api/v1/publishing/content/${f.itemId}/abandon`, { version: f.version }), onSuccess: refresh })
  const manual = useMutation({
    mutationFn: (v: { f: Failure; url: string; note: string }) => api.post(`/api/v1/publishing/content/${v.f.itemId}/mark-published`, { version: v.f.version, url: v.url, note: v.note || null }),
    onSuccess: refresh,
  })
  const [urls, setUrls] = useState<Record<string, string>>({}); const [notes, setNotes] = useState<Record<string, string>>({})
  return (
    <>
      {q.data?.length === 0 && <p className="muted">Nothing needs attention.</p>}
      {err(q.error ?? retry.error ?? abandon.error ?? manual.error) && <p className="error" role="alert">{err(q.error ?? retry.error ?? abandon.error ?? manual.error)}</p>}
      {q.data?.map((f) => (
        <div className="card" key={f.itemId}>
          <strong>{f.title}</strong> <span className="muted">· {f.channel} · v{f.version} · {new Date(f.updatedAt).toLocaleString()}</span>
          <p className="error">{f.reason}</p>
          {f.attempts.length > 0 && <ul className="muted">{f.attempts.map((a) => <li key={a.number}>Attempt {a.number}: {a.status}{a.error && ` · ${a.error}`}</li>)}</ul>}
          <div className="row">
            <button disabled={retry.isPending} onClick={() => retry.mutate(f)}>Retry</button>
            <button className="secondary" onClick={() => abandon.mutate(f)}>Stop trying (keep approved)</button>
            <Link to="/calendar">Open content</Link>
          </div>
          <div className="row">
            <label>It is already live: link to the post<input placeholder="https://…" value={urls[f.itemId] ?? ''} onChange={(e) => setUrls({ ...urls, [f.itemId]: e.target.value })} /></label>
            <label>Note<input value={notes[f.itemId] ?? ''} onChange={(e) => setNotes({ ...notes, [f.itemId]: e.target.value })} /></label>
            <button className="secondary" disabled={!urls[f.itemId]} onClick={() => manual.mutate({ f, url: urls[f.itemId], note: notes[f.itemId] ?? '' })}>Mark as published</button>
          </div>
        </div>
      ))}
    </>
  )
}

function useProductChoice() {
  const products = useQuery({ queryKey: ['products'], queryFn: () => api.get<Product[]>('/api/v1/products') })
  const [chosen, setChosen] = useState('')
  const pid = chosen || products.data?.find((p) => !p.isArchived)?.id || ''
  const select = (
    <label>Product
      <select value={pid} onChange={(e) => setChosen(e.target.value)}>{products.data?.filter((p) => !p.isArchived).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select>
    </label>
  )
  return { pid, select }
}

function Channels() {
  const qc = useQueryClient()
  const { pid, select } = useProductChoice()
  const caps = useQuery({ queryKey: ['pub-caps'], queryFn: () => api.get<Caps>('/api/v1/publishing/capabilities') })
  const accounts = useQuery({ queryKey: ['pub-accounts', pid], enabled: !!pid, queryFn: () => api.get<Account[]>(`/api/v1/publishing/products/${pid}/accounts`) })
  const [channel, setChannel] = useState('facebook')
  const refresh = () => void qc.invalidateQueries({ queryKey: ['pub-accounts', pid] })
  const sandbox = useMutation({ mutationFn: () => api.post(`/api/v1/publishing/products/${pid}/accounts/sandbox`, { channel, name: 'Sandbox page' }), onSuccess: refresh })
  const disconnect = useMutation({ mutationFn: (id: string) => api.post(`/api/v1/publishing/accounts/${id}/disconnect`), onSuccess: refresh })
  const startFb = useMutation({
    mutationFn: () => api.get<{ url: string }>(`/api/v1/publishing/connect/facebook/start?productId=${pid}&channel=${encodeURIComponent(channel)}`),
    onSuccess: (r) => { window.location.href = r.url },
  })
  return (
    <>
      <div className="row">{select}</div>
      <div className="card">
        <h2>Connected accounts</h2>
        {accounts.data?.length === 0 && <p className="muted">No account is connected for this product, so scheduled posts will fail with a clear message.</p>}
        {!!accounts.data?.length && (
          <table>
            <thead><tr><th>Channel</th><th>Account</th><th>Network</th><th>Status</th><th /></tr></thead>
            <tbody>{accounts.data.map((a) => (
              <tr key={a.id}><td>{a.channel}</td><td>{a.displayName}</td><td>{a.provider}</td>
                <td><span className={a.status === 'Connected' ? 'badge ok' : 'badge warn'}>{a.status === 'NeedsReconnect' ? 'Needs reconnecting' : a.status}</span>{a.lastError && <div className="muted">{a.lastError}</div>}</td>
                <td><button className="link" onClick={() => disconnect.mutate(a.id)}>Disconnect</button></td></tr>
            ))}</tbody>
          </table>
        )}
        {err(accounts.error ?? disconnect.error) && <p className="error" role="alert">{err(accounts.error ?? disconnect.error)}</p>}
      </div>
      <div className="card">
        <h2>Connect an account</h2>
        <p className="muted">The channel name must match the channel you pick on content items (for example “facebook”). Connecting a new account replaces the old one for that channel.</p>
        <div className="row">
          <label>Channel name<input value={channel} onChange={(e) => setChannel(e.target.value)} /></label>
          <button disabled={!caps.data?.facebookConfigured || !channel.trim() || startFb.isPending} onClick={() => startFb.mutate()}>Connect a Facebook Page</button>
          {caps.data?.sandboxEnabled && <button className="secondary" disabled={!channel.trim() || sandbox.isPending} onClick={() => sandbox.mutate()}>Connect sandbox (no real posting)</button>}
        </div>
        {caps.data && !caps.data.facebookConfigured && <p className="muted">Facebook is not set up on this server yet: it needs a Meta developer app (App ID, App Secret and the redirect address in the server settings). Pages can only be posted to by people who have a role on the page, and Meta’s app review is needed before anyone else can connect.</p>}
        {err(sandbox.error ?? startFb.error) && <p className="error" role="alert">{err(sandbox.error ?? startFb.error)}</p>}
      </div>
    </>
  )
}

interface CommentDto {
  id: string; status: string; authorName: string | null; text: string; commentedAt: string; category: string | null; draft: string | null; suggestHide: boolean
  missingFacts: string[]; replyText: string | null; repliedAt: string | null; hiddenAt: string | null; error: string | null; postUrl: string | null
}

/** Comments on the Page's posts. The AI only suggests; a person reads the exact reply, edits it and presses send. Nothing is ever answered automatically. */
function Comments() {
  const qc = useQueryClient()
  const { pid, select } = useProductChoice()
  const [show, setShow] = useState<'open' | 'done'>('open')
  const list = useQuery({ queryKey: ['comments', pid], enabled: !!pid, queryFn: () => api.get<CommentDto[]>(`/api/v1/publishing/products/${pid}/comments`) })
  const reload = () => void qc.invalidateQueries({ queryKey: ['comments', pid] })
  const refresh = useMutation({ mutationFn: () => api.post(`/api/v1/publishing/products/${pid}/comments/refresh`), onSuccess: () => setTimeout(reload, 6000) })
  const check = useMutation({ mutationFn: () => api.post<{ ok: boolean; problem: string | null; comments?: number }>(`/api/v1/publishing/products/${pid}/comments/check`) })
  const draftNew = useMutation({ mutationFn: () => api.post<{ considered: number; drafted: number; stoppedBecause: string | null }>(`/api/v1/publishing/products/${pid}/comments/draft-new`), onSuccess: reload })
  const rows = (list.data ?? []).filter((c) => (show === 'open' ? c.status === 'New' || c.status === 'Drafted' : c.status !== 'New' && c.status !== 'Drafted'))
  const waiting = (list.data ?? []).filter((c) => c.status === 'New').length
  return (
    <>
      <div className="row" style={{ alignItems: 'flex-end', flexWrap: 'wrap', gap: 12 }}>
        {select}
        <button className="secondary" disabled={refresh.isPending} onClick={() => refresh.mutate()}>{refresh.isSuccess ? 'Checking Facebook…' : 'Check for new comments'}</button>
        <button disabled={draftNew.isPending || waiting === 0} onClick={() => draftNew.mutate()}>{draftNew.isPending ? 'Drafting…' : `Suggest replies for new comments (${waiting})`}</button>
        <button className={show === 'open' ? 'link active' : 'link'} onClick={() => setShow('open')}>Waiting for you</button>
        <button className={show === 'done' ? 'link active' : 'link'} onClick={() => setShow('done')}>Done</button>
      </div>
      <p className="muted">New comments are fetched every 30 minutes. Suggestions use only the verified product facts. A reply is posted only when you press Send, once per comment, never in bulk.</p>
      {draftNew.data && <p className="muted">Suggested {draftNew.data.drafted} of {draftNew.data.considered}.{draftNew.data.stoppedBecause && ` Stopped: ${draftNew.data.stoppedBecause}`}</p>}
      {err(refresh.error ?? draftNew.error ?? list.error) && <p className="error" role="alert">{err(refresh.error ?? draftNew.error ?? list.error)}</p>}
      {list.isSuccess && rows.length === 0 && <p className="muted">{show === 'open' ? 'No comments are waiting.' : 'Nothing answered, hidden or ignored yet.'}</p>}
      {list.isSuccess && (list.data?.length ?? 0) === 0 && (
        <p><button className="secondary" disabled={check.isPending} onClick={() => check.mutate()}>{check.isPending ? 'Asking Facebook…' : 'Test the connection'}</button>{' '}
          {check.data && (check.data.ok ? <span className="badge ok">Facebook answered: {check.data.comments} comment{check.data.comments === 1 ? '' : 's'} on your newest post</span> : <span className="error">{check.data.problem}</span>)}</p>
      )}
      {rows.map((c) => <CommentCard key={c.id} c={c} onChanged={reload} />)}
    </>
  )
}

function CommentCard({ c, onChanged }: { c: CommentDto; onChanged: () => void }) {
  const [text, setText] = useState(c.draft ?? '')
  const [synced, setSynced] = useState(c.draft ?? '')
  if ((c.draft ?? '') !== synced) { setSynced(c.draft ?? ''); setText(c.draft ?? '') }   // a fresh suggestion replaces the box
  const base = `/api/v1/publishing/comments/${c.id}`
  const draft = useMutation({ mutationFn: () => api.post(`${base}/draft`), onSuccess: onChanged })
  const send = useMutation({ mutationFn: () => api.post(`${base}/send`, { text }), onSuccess: onChanged, onError: onChanged })
  const hide = useMutation({ mutationFn: () => api.post(`${base}/hide`), onSuccess: onChanged })
  const ignore = useMutation({ mutationFn: () => api.post(`${base}/ignore`), onSuccess: onChanged })
  const open = c.status === 'New' || c.status === 'Drafted'
  const busy = draft.isPending || send.isPending || hide.isPending || ignore.isPending
  const problem = err(draft.error ?? send.error ?? hide.error ?? ignore.error) ?? c.error
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <strong>{c.authorName ?? 'Someone'}</strong>
        <span className="muted">{new Date(c.commentedAt).toLocaleString()}{c.postUrl && <> · <a href={c.postUrl} target="_blank" rel="noreferrer">the post</a></>}</span>
      </div>
      <p style={{ whiteSpace: 'pre-wrap' }}>{c.text}</p>
      {c.category && <span className={c.category === 'Spam' || c.category === 'Abuse' ? 'badge warn' : 'badge'}>{c.category}</span>}
      {c.status === 'Replied' && <p><span className="badge ok">Replied</span> {c.repliedAt && new Date(c.repliedAt).toLocaleString()}<br /><span style={{ whiteSpace: 'pre-wrap' }}>{c.replyText}</span></p>}
      {c.status === 'Hidden' && <p><span className="badge warn">Hidden on Facebook</span></p>}
      {c.status === 'Ignored' && <p><span className="badge">Ignored</span></p>}
      {open && (
        <>
          {c.missingFacts.length > 0 && <p className="muted">The AI needs a fact it was not given, so it wrote no reply: {c.missingFacts.join('; ')}. Add it to the product profile, or write the reply yourself.</p>}
          {c.suggestHide && <p className="muted">This looks like spam or abuse. The AI wrote no reply. You can hide it, which keeps it visible only to its author and their friends.</p>}
          <label>Your reply<textarea rows={3} value={text} maxLength={8000} onChange={(e) => setText(e.target.value)} placeholder="Write a reply, or press Suggest a reply" /></label>
          <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
            <button className="secondary" disabled={busy} onClick={() => draft.mutate()}>{draft.isPending ? 'Thinking…' : c.draft ? 'Suggest again' : 'Suggest a reply'}</button>
            <button disabled={busy || text.trim().length === 0} onClick={() => { if (window.confirm('Post this reply publicly on your Facebook Page?\n\n' + text)) send.mutate() }}>{send.isPending ? 'Sending…' : 'Approve and send'}</button>
            <button className="secondary" disabled={busy} onClick={() => ignore.mutate()}>Ignore</button>
            <button className={c.suggestHide ? '' : 'secondary'} disabled={busy} onClick={() => { if (window.confirm('Hide this comment on Facebook?')) hide.mutate() }}>Hide on Facebook</button>
          </div>
        </>
      )}
      {problem && <p className="error" role="alert">{problem}</p>}
    </div>
  )
}

interface PageFigureDto { figure: string; value: number | null; day: string | null; metric: string | null; measuredAt: string | null; reason: string | null; change: number | null; changeSince: string | null; series: { day: string; value: number }[] }

const figureLabel: Record<string, { name: string; help: string }> = {
  followers: { name: 'Followers', help: 'People following the Page now' },
  new_follows: { name: 'New followers', help: 'Gained that day' },
  lost_follows: { name: 'Unfollows', help: 'Lost that day' },
  page_views: { name: 'Page views', help: 'Times Page content was shown that day' },
  page_reach: { name: 'Page reach', help: 'People who saw Page content that day' },
}

/** The Page's own figures from Facebook. A figure Facebook did not supply shows a dash and the reason, never a zero. */
function PageHealth({ pid }: { pid: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['page-health', pid], enabled: !!pid, queryFn: () => api.get<{ figures: PageFigureDto[] }>(`/api/v1/publishing/products/${pid}/page-health`) })
  const refresh = useMutation({ mutationFn: () => api.post(`/api/v1/publishing/products/${pid}/page-health/refresh`), onSuccess: () => setTimeout(() => void qc.invalidateQueries({ queryKey: ['page-health', pid] }), 8000) })
  const figs = q.data?.figures ?? []
  if (q.isSuccess && figs.every((f) => f.value === null && !f.reason)) return <p className="muted">No Page figures yet. They are pulled every 6 hours once a Facebook Page is connected with the read_insights permission.</p>
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h2>Page health</h2>
        <button className="secondary" disabled={refresh.isPending} onClick={() => refresh.mutate()}>{refresh.isSuccess ? 'Refreshing…' : 'Refresh now'}</button>
      </div>
      <div className="row" style={{ flexWrap: 'wrap', gap: 16 }}>
        {figs.map((f) => {
          const label = figureLabel[f.figure] ?? { name: f.figure, help: '' }
          const peak = Math.max(1, ...f.series.slice(-14).map((p) => p.value))
          return (
            <div key={f.figure} style={{ minWidth: 150 }} title={f.metric ? `Facebook metric: ${f.metric}` : undefined}>
              <div className="muted">{label.name}</div>
              <div style={{ fontSize: '1.6rem', fontWeight: 600 }}>{f.value === null ? '—' : f.value.toLocaleString()}</div>
              {f.figure === 'followers' && f.change !== null && f.changeSince && <div className={f.change >= 0 ? 'badge ok' : 'badge warn'}>{f.change >= 0 ? '+' : ''}{f.change} since {f.changeSince}</div>}
              {f.value !== null && f.figure !== 'followers' && f.day && <div className="muted">on {f.day}</div>}
              {f.value === null && f.reason && <div className="muted" style={{ maxWidth: 220 }}>Not supplied by Facebook: {f.reason}</div>}
              {f.series.length > 1 && f.figure !== 'followers' && (
                <div aria-hidden style={{ display: 'flex', alignItems: 'flex-end', gap: 2, height: 28, marginTop: 4 }}>
                  {f.series.slice(-14).map((p) => <div key={p.day} title={`${p.day}: ${p.value}`} style={{ width: 8, height: Math.max(2, (p.value / peak) * 28), background: 'var(--accent, #6366f1)', opacity: 0.7, borderRadius: 2 }} />)}
                </div>
              )}
            </div>
          )
        })}
      </div>
      <p className="muted">The newest day can still grow while Facebook finishes counting it.</p>
    </div>
  )
}

function Posts() {
  const { pid, select } = useProductChoice()
  const q = useQuery({ queryKey: ['pub-posts', pid], enabled: !!pid, queryFn: () => api.get<Post[]>(`/api/v1/publishing/products/${pid}/posts`) })
  return (
    <>
      <div className="row">{select}</div>
      {pid && <PageHealth pid={pid} />}
      {q.data?.length === 0 && <p className="muted">Nothing published yet.</p>}
      {!!q.data?.length && (
        <table>
          <thead><tr><th>When</th><th>Post</th><th>Recorded</th></tr></thead>
          <tbody>{q.data.map((p) => (
            <tr key={p.contentItemId + p.version}><td>{new Date(p.publishedAt).toLocaleString()}</td>
              <td>{p.url ? <a href={p.url} target="_blank" rel="noreferrer">{p.providerPostId}</a> : p.providerPostId}</td>
              <td>{p.source === 'Manual' ? <span className="badge warn">By hand{p.note && `: ${p.note}`}</span> : <span className="badge ok">Confirmed by the network</span>}</td></tr>
          ))}</tbody>
        </table>
      )}
    </>
  )
}

/** Where Facebook sends the browser back to: choose which page to connect. */
export function ConnectPage() {
  const [sp] = useSearchParams(); const nav = useNavigate()
  const pending = sp.get('pending'); const error = sp.get('error')
  const pages = useQuery({ queryKey: ['pub-pending', pending], enabled: !!pending, retry: false, queryFn: () => api.get<{ id: string; name: string }[]>(`/api/v1/publishing/connect/pending/${pending}`) })
  const pick = useMutation({ mutationFn: (pageId: string) => api.post(`/api/v1/publishing/connect/pending/${pending}/select`, { pageId }), onSuccess: () => nav('/publishing') })
  const reasons: Record<string, string> = {
    denied: 'Facebook did not give permission, so nothing was connected.', expired: 'That connection attempt expired. Please start again.',
    nopages: 'That Facebook account does not manage any Pages.', facebook: 'Facebook could not complete the connection. Please try again.',
  }
  return (
    <>
      <h1>Connect a Facebook Page</h1>
      {error && <p className="error" role="alert">{reasons[error] ?? 'The connection failed.'}</p>}
      {pending && pages.isError && <p className="error" role="alert">This connection link is no longer valid.</p>}
      {pages.data && (
        <div className="card">
          <p>Choose the Page this product should post to:</p>
          {pages.data.map((p) => <div className="row" key={p.id}><strong>{p.name}</strong><button disabled={pick.isPending} onClick={() => pick.mutate(p.id)}>Use this Page</button></div>)}
          {err(pick.error) && <p className="error" role="alert">{err(pick.error)}</p>}
        </div>
      )}
      <p><Link to="/publishing">Back to publishing</Link></p>
    </>
  )
}
