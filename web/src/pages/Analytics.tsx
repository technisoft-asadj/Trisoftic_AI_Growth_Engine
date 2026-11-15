import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type Product } from '../api.ts'

interface Tile {
  key: string; name: string; category: string; unit: string; definition: string; status: string; value: number | null
  valuesByCurrency: { currency: string; value: number }[]; latest: number | null; latestPeriodEnd?: string; aggregation: string
  periods: number; unavailablePeriods: number; conflicts: number; sources: string[]; oldestRetrieval?: string; stale: boolean
}
interface Summary { meta: { from: string; to: string; asOf: string; notes: string[] }; sources: { id: string; name: string; kind: string; lastRetrievedAt?: string; stale: boolean; freshnessHours: number }[]; tiles: Tile[] }
interface Source { id: string; name: string; kind: string; stale: boolean; lastRetrievedAt?: string; observations: number }
interface Batch { id: string; fileName: string; source?: string; status: string; imported: number; createdAt: string; rolledBackAt?: string }
interface ImportResult { dryRun: boolean; batchId?: string; rowsInFile: number; wouldImport: number; imported: number; unavailableValues: number; rejected: number; errors: { row: number; message: string }[]; note?: string }

const err = (e: unknown) => (e instanceof ApiError ? e.message : e ? 'Something went wrong' : null)
const dash = '—'
const VIEWS: Record<string, string[] | null> = {
  all: null,
  consumer: ['impressions', 'reach', 'video_views', 'followers', 'engagements', 'clicks', 'installs', 'signups', 'retention_d1', 'retention_d7', 'spend', 'revenue'],
  b2b: ['impressions', 'reach', 'followers', 'engagements', 'clicks', 'ctr', 'sessions', 'leads', 'conversion_rate', 'spend', 'revenue'],
}

function fmt(t: Tile): React.ReactNode {
  const n = (v: number | null) => (v == null ? dash : t.unit === 'Percent' ? `${v}%` : v.toLocaleString(undefined, { maximumFractionDigits: 2 }))
  if (t.unit === 'Money') return t.valuesByCurrency.length ? t.valuesByCurrency.map((c) => <div key={c.currency}>{c.currency} {c.value.toLocaleString(undefined, { maximumFractionDigits: 2 })}</div>) : dash
  if (t.aggregation === 'None') return t.latest == null ? dash : <>{n(t.latest)} <span className="muted" style={{ fontSize: '.8rem' }}>latest</span></>
  return n(t.value)
}

export function AnalyticsPage() {
  const products = useQuery({ queryKey: ['products'], queryFn: () => api.get<Product[]>('/api/v1/products') })
  const [chosen, setChosen] = useState(''); const [days, setDays] = useState(30); const [channel, setChannel] = useState(''); const [view, setView] = useState('all'); const [tab, setTab] = useState<'dash' | 'data'>('dash')
  const pid = chosen || products.data?.find((p) => !p.isArchived)?.id || ''
  return (
    <>
      <h1>Analytics</h1>
      <div className="row">
        <label>Product<select value={pid} onChange={(e) => setChosen(e.target.value)}>{products.data?.filter((p) => !p.isArchived).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
        <div className="tabs" style={{ margin: 0 }}>
          <button className={tab === 'dash' ? 'active link' : 'link'} onClick={() => setTab('dash')}>Dashboard</button>
          <button className={tab === 'data' ? 'active link' : 'link'} onClick={() => setTab('data')}>Data and imports</button>
        </div>
      </div>
      {!pid ? <p className="muted">Create a product first.</p> : tab === 'dash'
        ? <Dashboard pid={pid} days={days} setDays={setDays} channel={channel} setChannel={setChannel} view={view} setView={setView} />
        : <DataTab pid={pid} />}
    </>
  )
}

function Dashboard({ pid, days, setDays, channel, setChannel, view, setView }: { pid: string; days: number; setDays: (n: number) => void; channel: string; setChannel: (s: string) => void; view: string; setView: (s: string) => void }) {
  const q = useQuery({
    queryKey: ['an-summary', pid, days, channel],
    queryFn: () => {
      const to = new Date(); const from = new Date(to.getTime() - (days - 1) * 86_400_000)
      const d = (x: Date) => x.toISOString().slice(0, 10)
      return api.get<Summary>(`/api/v1/analytics/summary?productId=${pid}&from=${d(from)}&to=${d(to)}${channel ? `&channel=${encodeURIComponent(channel)}` : ''}`)
    },
  })
  const keep = VIEWS[view]
  const tiles = (q.data?.tiles ?? []).filter((t) => !keep || keep.includes(t.key))
  const cats = [...new Set(tiles.map((t) => t.category))]
  const anyData = tiles.some((t) => t.status !== 'NoData')
  return (
    <>
      <div className="row">
        <label>Period<select value={days} onChange={(e) => setDays(Number(e.target.value))}><option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option></select></label>
        <label>Channel<input placeholder="All channels" value={channel} onChange={(e) => setChannel(e.target.value)} /></label>
        <label>View<select value={view} onChange={(e) => setView(e.target.value)}><option value="all">All metrics</option><option value="consumer">Consumer app (games, B2C)</option><option value="b2b">B2B software</option></select></label>
      </div>
      {err(q.error) && <p className="error" role="alert">{err(q.error)}</p>}
      {q.data && <ul className="muted">{q.data.meta.notes.map((n) => <li key={n}>{n}</li>)}</ul>}
      {q.data && !anyData && <p>No data yet for this product and period. Add a data source and import a sheet under “Data and imports”. B2B lead and deal numbers are on the <Link to="/crm/dashboard">CRM dashboard</Link>.</p>}
      {cats.map((c) => (
        <div key={c}>
          <h2>{c}</h2>
          <div className="grid">
            {tiles.filter((t) => t.category === c).map((t) => (
              <div className="card tile" key={t.key} title={t.definition}>
                <span className="muted">{t.name} <span title={t.definition} style={{ cursor: 'help' }}>ⓘ</span></span>
                <strong style={{ fontSize: '1.4rem' }}>{fmt(t)}</strong>
                {t.status === 'NoData' && <span className="muted">No data in this period</span>}
                {t.status === 'Unavailable' && <span className="badge warn">Source could not measure it</span>}
                {t.status === 'Partial' && <span className="badge warn">{t.unavailablePeriods} of {t.periods} periods missing</span>}
                {t.status !== 'NoData' && <span className="muted" style={{ fontSize: '.8rem' }}>{t.sources.join(', ')}{t.oldestRetrieval && ` · retrieved ${new Date(t.oldestRetrieval).toLocaleDateString()}`}</span>}
                {t.stale && <span className="badge warn">Stale source</span>}
                {t.conflicts > 0 && <span className="badge warn">{t.conflicts} period(s) where sources disagree</span>}
              </div>
            ))}
          </div>
        </div>
      ))}
    </>
  )
}

function DataTab({ pid }: { pid: string }) {
  const qc = useQueryClient()
  const sources = useQuery({ queryKey: ['an-sources', pid], queryFn: () => api.get<Source[]>(`/api/v1/analytics/sources?productId=${pid}`) })
  const batches = useQuery({ queryKey: ['an-batches', pid], queryFn: () => api.get<Batch[]>(`/api/v1/analytics/products/${pid}/imports`) })
  const [name, setName] = useState(''); const [hours, setHours] = useState('840')
  const [sourceId, setSourceId] = useState(''); const [file, setFile] = useState<File | null>(null)
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['an-sources', pid] }); void qc.invalidateQueries({ queryKey: ['an-batches', pid] }); void qc.invalidateQueries({ queryKey: ['an-summary'] }) }
  const addSource = useMutation({ mutationFn: () => api.post(`/api/v1/analytics/sources`, { productId: pid, name, kind: 'Manual', freshnessHours: Number(hours) }), onSuccess: () => { setName(''); refresh() } })
  const run = useMutation({
    mutationFn: (dry: boolean) => { const fd = new FormData(); fd.append('file', file!); return api.upload<ImportResult>(`/api/v1/analytics/products/${pid}/imports?sourceId=${sourceId || sources.data?.[0]?.id}&dryRun=${dry}`, fd) },
    onSuccess: (r) => { if (!r.dryRun) refresh() },
  })
  const rollback = useMutation({ mutationFn: (id: string) => api.post(`/api/v1/analytics/imports/${id}/rollback`), onSuccess: refresh })
  const r = run.data
  return (
    <>
      <div className="card">
        <h2>Data sources</h2>
        <p className="muted">Every number comes from a named source. A source goes stale when nothing has been added for longer than its window.</p>
        {sources.data?.length === 0 && <p className="muted">No sources yet. Add one, then import a sheet into it.</p>}
        {!!sources.data?.length && (
          <table><thead><tr><th>Name</th><th>Type</th><th>Last data</th><th>Figures</th></tr></thead>
            <tbody>{sources.data.map((s) => <tr key={s.id}><td>{s.name}</td><td>{s.kind}</td><td>{s.lastRetrievedAt ? new Date(s.lastRetrievedAt).toLocaleString() : 'never'} {s.stale && <span className="badge warn">Stale</span>}</td><td>{s.observations}</td></tr>)}</tbody></table>
        )}
        <div className="row">
          <label>New manual source<input placeholder="e.g. Meta Ads export" value={name} onChange={(e) => setName(e.target.value)} /></label>
          <label>Stale after (hours)<input type="number" min={1} value={hours} onChange={(e) => setHours(e.target.value)} /></label>
          <button disabled={!name.trim() || addSource.isPending} onClick={() => addSource.mutate()}>Add source</button>
        </div>
        {err(addSource.error) && <p className="error" role="alert">{err(addSource.error)}</p>}
      </div>

      <div className="card">
        <h2>Import a sheet (CSV)</h2>
        <p className="muted">Columns: <code>metric, periodStart, value</code>; optional <code>periodEnd, channel, market, campaignId, currency</code>. Dates are YYYY-MM-DD. Money needs a currency. Leave a value blank (or write n/a) when the source could not measure it; it is stored as “unavailable”, never as zero. Existing figures are never overwritten: roll the earlier import back first.</p>
        <div className="row">
          <label>Into source<select value={sourceId} onChange={(e) => setSourceId(e.target.value)}>{sources.data?.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
          <input type="file" accept=".csv" onChange={(e) => { setFile(e.target.files?.[0] ?? null); run.reset() }} />
          <button className="secondary" disabled={!file || !sources.data?.length || run.isPending} onClick={() => run.mutate(true)}>Preview (nothing saved)</button>
          <button disabled={!file || !sources.data?.length || run.isPending} onClick={() => run.mutate(false)}>Import</button>
        </div>
        {err(run.error) && <p className="error" role="alert">{err(run.error)}</p>}
        {r && (
          <>
            <p><strong>{r.dryRun ? 'Preview' : 'Imported'}:</strong> {r.rowsInFile} rows · {r.dryRun ? r.wouldImport : r.imported} {r.dryRun ? 'would be imported' : 'imported'} · {r.unavailableValues} stored as unavailable · {r.rejected} rejected</p>
            {r.errors.length > 0 && <div className="error"><ul>{r.errors.slice(0, 30).map((e) => <li key={e.row}>Row {e.row}: {e.message}</li>)}</ul>{r.note && <p className="muted">{r.note}</p>}</div>}
          </>
        )}
      </div>

      <div className="card">
        <h2>Import history</h2>
        {batches.data?.length === 0 && <p className="muted">Nothing imported yet.</p>}
        {!!batches.data?.length && (
          <table><tbody>{batches.data.map((b) => (
            <tr key={b.id}><td>{new Date(b.createdAt).toLocaleString()}</td><td>{b.fileName}</td><td>{b.source}</td><td>{b.imported} figures</td>
              <td>{b.status === 'RolledBack' ? <span className="badge warn">Rolled back</span> : <button className="link" onClick={() => rollback.mutate(b.id)}>Roll back</button>}</td></tr>
          ))}</tbody></table>
        )}
        {err(rollback.error) && <p className="error" role="alert">{err(rollback.error)}</p>}
      </div>
    </>
  )
}
