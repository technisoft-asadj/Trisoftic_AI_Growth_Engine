import { useEffect, useState, type FormEvent } from 'react'
import { Link, NavLink, Navigate, Route, Routes, useParams, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type AssetItem, type Fact, type Claim, type Grounded, type Product, type Profile, type ProfileVersion } from '../api.ts'
import { useAuth } from '../auth.tsx'
import { StrategiesTab } from './Strategies.tsx'
import { CampaignsTab, ContentTab } from './Content.tsx'

const emptyProfile = (): Profile => ({ summary: '', facts: [], claims: [], markets: [], platforms: [], personas: [], brandVoice: { dos: [], donts: [] }, links: [], competitors: [] })
const lines = (t: string) => t.split('\n').map((s) => s.trim()).filter(Boolean)
const csv = (t: string) => t.split(',').map((s) => s.trim()).filter(Boolean)

function ErrorBox({ error }: { error: unknown }) {
  if (!(error instanceof ApiError)) return error ? <p className="error">Something went wrong.</p> : null
  return (
    <div className="error" role="alert">
      <div>{error.message}</div>
      {error.violations.length > 0 && <ul>{error.violations.map((v) => <li key={v}>{v}</li>)}</ul>}
    </div>
  )
}

export function ProductPage() {
  const { id } = useParams()
  const { can } = useAuth()
  const product = useQuery({ queryKey: ['product', id], queryFn: () => api.get<Product>(`/api/v1/products/${id}`) })
  if (product.isLoading) return <p className="muted">Loading…</p>
  if (!product.data) return <p>Product not found, or you do not have access.</p>
  const p = product.data
  return (
    <>
      <h1>{p.name}</h1>
      <p className="muted">{p.kind} · {p.lifecycle} · {p.approvedProfileVersion ? `approved profile v${p.approvedProfileVersion}` : 'no approved profile yet'}</p>
      <nav className="tabs">
        <NavLink to="profile">Profile</NavLink>
        {can('assets.read') && <NavLink to="assets">Assets</NavLink>}
        {can('products.read') && <NavLink to="strategies">Strategies</NavLink>}
        {can('products.read') && <NavLink to="campaigns">Campaigns</NavLink>}
        {can('products.read') && <NavLink to="content">Content</NavLink>}
        {can('ai.generate') && <NavLink to="generate">AI studio</NavLink>}
      </nav>
      <Routes>
        <Route index element={<Navigate to="profile" replace />} />
        <Route path="profile" element={<ProfileTab productId={p.id} />} />
        <Route path="assets" element={<AssetsTab productId={p.id} />} />
        <Route path="strategies" element={<StrategiesRoute productId={p.id} />} />
        <Route path="campaigns" element={<CampaignsTab productId={p.id} />} />
        <Route path="content" element={<ContentTab productId={p.id} />} />
        <Route path="generate" element={<GenerateTab product={p} />} />
      </Routes>
    </>
  )
}

function StrategiesRoute({ productId }: { productId: string }) {
  const [params, setParams] = useSearchParams()
  return <StrategiesTab productId={productId} draftToAdopt={params.get('draft')} onAdopted={() => setParams({})} />
}

// ---------------- Profile ----------------

function ProfileTab({ productId }: { productId: string }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const versions = useQuery({ queryKey: ['versions', productId], queryFn: () => api.get<ProfileVersion[]>(`/api/v1/products/${productId}/profile/versions`) })
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['versions', productId] }); void qc.invalidateQueries({ queryKey: ['product', productId] }); void qc.invalidateQueries({ queryKey: ['approvals'] }) }
  const create = useMutation({ mutationFn: () => api.post(`/api/v1/products/${productId}/profile/versions`), onSuccess: refresh })
  const submit = useMutation({ mutationFn: (n: number) => api.post(`/api/v1/products/${productId}/profile/versions/${n}/submit`), onSuccess: refresh })
  const withdraw = useMutation({ mutationFn: (n: number) => api.post(`/api/v1/products/${productId}/profile/versions/${n}/withdraw`), onSuccess: refresh })

  if (versions.isLoading) return <p className="muted">Loading…</p>
  const list = versions.data ?? []
  const open = list.find((v) => v.status === 'Draft' || v.status === 'InReview')
  const shown = open ?? list[0]
  const canWrite = can('products.write')

  return (
    <>
      <div className="row">
        {canWrite && !open && <button onClick={() => create.mutate()} disabled={create.isPending}>{list.length ? 'New draft from latest' : 'Start the first profile'}</button>}
        {open?.status === 'Draft' && canWrite && <button onClick={() => submit.mutate(open.number)} disabled={submit.isPending}>Submit v{open.number} for approval</button>}
        {open?.status === 'InReview' && canWrite && <button className="secondary" onClick={() => withdraw.mutate(open.number)}>Withdraw v{open.number}</button>}
      </div>
      <ErrorBox error={create.error ?? submit.error ?? withdraw.error} />
      {!shown && <p className="muted">No profile yet. AI can only write about a product once its profile has been approved.</p>}
      {shown && (
        <>
          <p><span className={`badge ${shown.status === 'Approved' ? 'ok' : shown.status === 'Draft' ? '' : 'warn'}`}>v{shown.number} · {shown.status}</span>
            {shown.decisionComment && <span className="muted"> Reviewer: “{shown.decisionComment}”</span>}</p>
          {shown.status === 'Draft' && canWrite
            ? <ProfileEditor key={shown.id + shown.number} productId={productId} version={shown} onSaved={refresh} />
            : <ProfileView profile={shown.content} />}
        </>
      )}
      {list.length > 1 && <p className="muted">History: {list.map((v) => `v${v.number} ${v.status}`).join(' · ')}</p>}
    </>
  )
}

function ProfileView({ profile }: { profile: Profile }) {
  return (
    <div className="card">
      <p>{profile.summary || <span className="muted">No summary</span>}</p>
      <h3>Facts</h3>
      <ul>{profile.facts.map((f) => <li key={f.id}>{f.verified ? '✔' : '○'} <strong>{f.category}</strong> — {f.text} <span className="muted">{f.evidence && `(${f.evidence})`}</span></li>)}</ul>
      <h3>Claims</h3>
      <ul>{profile.claims.map((c) => <li key={c.id}>{c.status === 'Prohibited' ? '⛔' : '✔'} {c.text}</li>)}</ul>
      <h3>Markets</h3>
      <ul>{profile.markets.map((m) => <li key={m.country}>{m.country}: {m.languages.join(', ')}</li>)}</ul>
    </div>
  )
}

function ProfileEditor({ productId, version, onSaved }: { productId: string; version: ProfileVersion; onSaved: () => void }) {
  const [p, setP] = useState<Profile>({ ...emptyProfile(), ...version.content })
  const [note, setNote] = useState(version.changeNote ?? '')
  const save = useMutation({
    mutationFn: () => api.put(`/api/v1/products/${productId}/profile/versions/${version.number}`, { content: p, changeNote: note }),
    onSuccess: onSaved,
  })
  const setFact = (i: number, patch: Partial<Fact>) => setP({ ...p, facts: p.facts.map((f, j) => (j === i ? { ...f, ...patch } : f)) })
  const setClaim = (i: number, patch: Partial<Claim>) => setP({ ...p, claims: p.claims.map((c, j) => (j === i ? { ...c, ...patch } : c)) })

  return (
    <form className="card" onSubmit={(e: FormEvent) => { e.preventDefault(); save.mutate() }}>
      <label>Summary<textarea rows={3} value={p.summary} onChange={(e) => setP({ ...p, summary: e.target.value })} /></label>

      <h3>Verified facts</h3>
      <p className="muted">Only facts marked verified (with an evidence note) can be used by the AI.</p>
      {p.facts.map((f, i) => (
        <div className="row fact" key={f.id || i}>
          <select value={f.category} onChange={(e) => setFact(i, { category: e.target.value })}>
            {['Feature', 'Benefit', 'Differentiator', 'Pricing', 'Integration', 'Platform', 'Audience', 'Other'].map((c) => <option key={c}>{c}</option>)}
          </select>
          <input placeholder="Fact" value={f.text} onChange={(e) => setFact(i, { text: e.target.value })} />
          <input placeholder="Evidence / source" value={f.evidence ?? ''} onChange={(e) => setFact(i, { evidence: e.target.value })} />
          <label className="inline"><input type="checkbox" checked={f.verified} onChange={(e) => setFact(i, { verified: e.target.checked })} />verified</label>
          <button type="button" className="link" onClick={() => setP({ ...p, facts: p.facts.filter((_, j) => j !== i) })}>remove</button>
        </div>
      ))}
      <button type="button" className="secondary" onClick={() => setP({ ...p, facts: [...p.facts, { id: '', category: 'Feature', text: '', verified: false }] })}>Add fact</button>

      <h3>Claims</h3>
      {p.claims.map((c, i) => (
        <div className="row fact" key={c.id || i}>
          <select value={c.status} onChange={(e) => setClaim(i, { status: e.target.value as Claim['status'] })}><option>Approved</option><option>Prohibited</option></select>
          <input placeholder="Claim text" value={c.text} onChange={(e) => setClaim(i, { text: e.target.value })} />
          <input placeholder="Evidence (required if approved)" value={c.evidence ?? ''} onChange={(e) => setClaim(i, { evidence: e.target.value })} />
          <button type="button" className="link" onClick={() => setP({ ...p, claims: p.claims.filter((_, j) => j !== i) })}>remove</button>
        </div>
      ))}
      <button type="button" className="secondary" onClick={() => setP({ ...p, claims: [...p.claims, { id: '', text: '', status: 'Prohibited' }] })}>Add claim</button>

      <h3>Markets &amp; platforms</h3>
      <label>Markets (one per line, e.g. <code>PK: en, ur</code>)
        <textarea rows={3} value={p.markets.map((m) => `${m.country}: ${m.languages.join(', ')}`).join('\n')}
          onChange={(e) => setP({ ...p, markets: lines(e.target.value).map((l) => { const [c, ls = ''] = l.split(':'); return { country: c.trim(), languages: csv(ls) } }) })} />
      </label>
      <label>Platforms (comma separated)<input value={p.platforms.join(', ')} onChange={(e) => setP({ ...p, platforms: csv(e.target.value) })} /></label>

      <h3>Brand voice</h3>
      <label>Tone<input value={p.brandVoice.tone ?? ''} onChange={(e) => setP({ ...p, brandVoice: { ...p.brandVoice, tone: e.target.value } })} /></label>
      <div className="row">
        <label>Do (one per line)<textarea rows={3} value={p.brandVoice.dos.join('\n')} onChange={(e) => setP({ ...p, brandVoice: { ...p.brandVoice, dos: lines(e.target.value) } })} /></label>
        <label>Don’t (one per line)<textarea rows={3} value={p.brandVoice.donts.join('\n')} onChange={(e) => setP({ ...p, brandVoice: { ...p.brandVoice, donts: lines(e.target.value) } })} /></label>
      </div>

      <label>Change note<input value={note} onChange={(e) => setNote(e.target.value)} /></label>
      <ErrorBox error={save.error} />
      <button type="submit" disabled={save.isPending}>{save.isPending ? 'Saving…' : 'Save draft'}</button>
      {save.isSuccess && <span className="muted"> Saved.</span>}
    </form>
  )
}

// ---------------- Assets ----------------

function AssetsTab({ productId }: { productId: string }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const assets = useQuery({ queryKey: ['assets', productId], queryFn: () => api.get<AssetItem[]>(`/api/v1/products/${productId}/assets`) })
  const upload = useMutation({
    mutationFn: (f: FormData) => api.upload(`/api/v1/products/${productId}/assets`, f),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['assets', productId] }),
  })
  function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    upload.mutate(new FormData(e.currentTarget))
    e.currentTarget.reset()
  }
  return (
    <>
      {assets.data?.length === 0 && <p className="muted">No assets yet.</p>}
      <table>
        <thead><tr><th>Name</th><th>Kind</th><th>Ver</th><th>Source</th><th>Rights</th><th /></tr></thead>
        <tbody>
          {assets.data?.map((a) => (
            <tr key={a.id}>
              <td>{a.name} {a.isSynthetic && <span className="badge warn">synthetic</span>}</td>
              <td>{a.kind}</td><td>{a.version}</td><td>{a.source}</td><td>{a.rights}</td>
              <td><a href={`/api/v1/assets/${a.id}/content`} target="_blank" rel="noreferrer">open</a></td>
            </tr>
          ))}
        </tbody>
      </table>
      {can('assets.write') && (
        <form className="card" onSubmit={onSubmit}>
          <h2>Upload</h2>
          <p className="muted">PNG, JPG, GIF, WebP, PDF, MP4, WebM. Source and usage rights are required.</p>
          <div className="row">
            <input type="file" name="file" required />
            <select name="kind" required defaultValue="Screenshot">{['Logo', 'Screenshot', 'GameplayFootage', 'ProductDemo', 'Image', 'Video', 'Template', 'Document', 'Other'].map((k) => <option key={k}>{k}</option>)}</select>
            <select name="source" required defaultValue=""><option value="" disabled>Source…</option>{['Own', 'Licensed', 'ThirdParty', 'AiGenerated'].map((k) => <option key={k}>{k}</option>)}</select>
            <select name="rights" required defaultValue=""><option value="" disabled>Rights…</option>{['Unknown', 'InternalOnly', 'Unrestricted', 'LicensedWithLimits'].map((k) => <option key={k}>{k}</option>)}</select>
            <button type="submit" disabled={upload.isPending}>Upload</button>
          </div>
          <ErrorBox error={upload.error} />
        </form>
      )}
    </>
  )
}

// ---------------- AI studio ----------------

function GenerateTab({ product }: { product: Product }) {
  const [kind, setKind] = useState<'ContentDraft' | 'StrategyBrief'>('ContentDraft')
  const [brief, setBrief] = useState('')
  const [channel, setChannel] = useState('')
  const [language, setLanguage] = useState('')
  const gen = useMutation({
    mutationFn: () => api.post<Grounded>(`/api/v1/products/${product.id}/ai/generate`, { kind, brief, channel: channel || null, language: language || null }),
  })
  useEffect(() => { gen.reset() }, [kind]) // eslint-disable-line react-hooks/exhaustive-deps
  const d = gen.data

  return (
    <>
      {!product.approvedProfileVersion && <p className="error">This product has no approved profile, so the AI cannot write about it yet.</p>}
      <form className="card" onSubmit={(e) => { e.preventDefault(); gen.mutate() }}>
        <div className="row">
          <label>Type<select value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}><option value="ContentDraft">Content draft</option><option value="StrategyBrief">Strategy brief</option></select></label>
          <label>Channel / format<input value={channel} onChange={(e) => setChannel(e.target.value)} placeholder="Facebook post" /></label>
          <label>Language<input value={language} onChange={(e) => setLanguage(e.target.value)} placeholder="en (must be a verified market language)" /></label>
        </div>
        <label>Brief<textarea rows={4} value={brief} onChange={(e) => setBrief(e.target.value)} required maxLength={4000} /></label>
        <button type="submit" disabled={gen.isPending || !product.approvedProfileVersion}>{gen.isPending ? 'Generating… (can take a minute)' : 'Generate'}</button>
      </form>
      <ErrorBox error={gen.error} />
      {d && (
        <div className="card">
          <h2>{d.draft.title}</h2>
          <pre className="draft">{d.draft.body}</pre>
          <h3>Claims &amp; sources</h3>
          <ul>{d.draft.claims.map((c, i) => <li key={i}>{c.text} — {c.assumption ? <span className="badge warn">assumption</span> : <code>{c.factRef}</code>}</li>)}</ul>
          {d.draft.questions.length > 0 && (<><h3>The AI needs more information</h3><ul>{d.draft.questions.map((q) => <li key={q}>{q}</li>)}</ul></>)}
          <p><Link to={`../strategies?draft=${d.draftId}`}>Create a strategy from this draft →</Link></p>
          <p className="muted">Based on profile v{d.profileVersion} · {d.provider}/{d.model} · ${d.costUsd.toFixed(4)} · {d.attempts} attempt(s). Saved as an AI draft; nothing is published, and a human must review it.</p>
        </div>
      )}
    </>
  )
}
