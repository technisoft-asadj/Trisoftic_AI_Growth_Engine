import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../api.ts'
import { useAuth } from '../auth.tsx'

interface Kpi { name: string; target?: string }
interface Hypothesis { statement: string; evidence?: string; status: string }
interface Content {
  objective: string; audience: string; market: string; language: string; channels: string[]; positioning: string; keyMessages: string[]; cta: string
  durationDays?: number | null; cadence: string; budgetUsd?: number | null; kpis: Kpi[]; hypotheses: Hypothesis[]; assumptions: string[]; risks: string[]; notes: string
}
interface Template { key: string; name: string; description: string }
interface Summary { id: string; name: string; templateKey: string; currentVersion: number }
interface Detail { id: string; name: string; currentVersion: number; isArchived: boolean; current: Content; versions: { number: number; note?: string; sourceDraftId?: string; createdAt: string }[] }

const lines = (t: string) => t.split('\n').map((s) => s.trim()).filter(Boolean)

export function StrategiesTab({ productId, draftToAdopt, onAdopted }: { productId: string; draftToAdopt?: string | null; onAdopted?: () => void }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const [open, setOpen] = useState<string | null>(null)
  const list = useQuery({ queryKey: ['strategies', productId], queryFn: () => api.get<Summary[]>(`/api/v1/products/${productId}/strategies`) })
  const templates = useQuery({ queryKey: ['strategy-templates'], queryFn: () => api.get<Template[]>('/api/v1/strategy-templates') })
  const [name, setName] = useState('')
  const [templateKey, setTemplateKey] = useState('blank')
  const create = useMutation({
    mutationFn: () => api.post<{ id: string }>(`/api/v1/products/${productId}/strategies`, { name, templateKey, sourceDraftId: draftToAdopt ?? null }),
    onSuccess: (r) => { setName(''); setOpen(r.id); onAdopted?.(); void qc.invalidateQueries({ queryKey: ['strategies', productId] }) },
  })
  const canWrite = can('campaigns.manage')

  if (open) return <StrategyEditor id={open} canWrite={canWrite} onBack={() => setOpen(null)} />
  return (
    <>
      {draftToAdopt && <p className="badge ok">An AI draft is selected: creating a strategy will copy it into the notes.</p>}
      {list.data?.length === 0 && <p className="muted">No strategies yet.</p>}
      <ul>{list.data?.map((s) => <li key={s.id}><button className="link" onClick={() => setOpen(s.id)}>{s.name}</button> <span className="muted">v{s.currentVersion} · {s.templateKey}</span></li>)}</ul>
      {canWrite && (
        <form className="card" onSubmit={(e: FormEvent) => { e.preventDefault(); create.mutate() }}>
          <h2>New strategy</h2>
          <div className="row">
            <label>Name<input value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} /></label>
            <label>Template
              <select value={templateKey} onChange={(e) => setTemplateKey(e.target.value)}>
                {templates.data?.map((t) => <option key={t.key} value={t.key}>{t.name}: {t.description}</option>)}
              </select>
            </label>
            <button type="submit" disabled={create.isPending}>Create</button>
          </div>
          {create.error instanceof ApiError && <p className="error">{create.error.message}</p>}
        </form>
      )}
    </>
  )
}

function StrategyEditor({ id, canWrite, onBack }: { id: string; canWrite: boolean; onBack: () => void }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['strategy', id], queryFn: () => api.get<Detail>(`/api/v1/strategies/${id}`) })
  if (!q.data) return <p className="muted">Loading…</p>
  // key on the version so the form resets to the latest saved version after every save or reload
  return <EditorForm key={`${id}-${q.data.currentVersion}`} d={q.data} canWrite={canWrite && !q.data.isArchived} onBack={onBack}
    onSaved={() => { void qc.invalidateQueries({ queryKey: ['strategy', id] }); void qc.invalidateQueries({ queryKey: ['strategies'] }) }} />
}

function EditorForm({ d, canWrite, onBack, onSaved }: { d: Detail; canWrite: boolean; onBack: () => void; onSaved: () => void }) {
  const [c, setC] = useState<Content>({ ...d.current })
  const [note, setNote] = useState('')
  const save = useMutation({
    mutationFn: () => api.post(`/api/v1/strategies/${d.id}/versions`, { content: c, note: note || null, baseVersion: d.currentVersion }),
    onSuccess: onSaved,
  })
  const set = <K extends keyof Content>(k: K, v: Content[K]) => setC({ ...c, [k]: v })
  const text = (k: 'objective' | 'audience' | 'market' | 'language' | 'positioning' | 'cta' | 'cadence', label: string) =>
    <label>{label}<input disabled={!canWrite} value={c[k]} onChange={(e) => set(k, e.target.value)} /></label>

  return (
    <>
      <button className="link" onClick={onBack}>← Back to strategies</button>
      <h2>{d.name} <span className="muted">v{d.currentVersion}</span></h2>
      <form className="card" onSubmit={(e: FormEvent) => { e.preventDefault(); save.mutate() }}>
        <div className="row">{text('objective', 'Objective')}{text('audience', 'Audience')}</div>
        <div className="row">{text('market', 'Market')}{text('language', 'Language')}{text('cadence', 'Cadence')}</div>
        <div className="row">{text('positioning', 'Positioning')}{text('cta', 'Call to action')}</div>
        <div className="row">
          <label>Duration (days)<input type="number" min={1} disabled={!canWrite} value={c.durationDays ?? ''} onChange={(e) => set('durationDays', e.target.value ? Number(e.target.value) : null)} /></label>
          <label>Budget (USD, a plan not a spend)<input type="number" min={0} step="0.01" disabled={!canWrite} value={c.budgetUsd ?? ''} onChange={(e) => set('budgetUsd', e.target.value ? Number(e.target.value) : null)} /></label>
        </div>
        <label>Channels (one per line)<textarea rows={2} disabled={!canWrite} value={c.channels.join('\n')} onChange={(e) => set('channels', lines(e.target.value))} /></label>
        <label>Key messages (one per line)<textarea rows={3} disabled={!canWrite} value={c.keyMessages.join('\n')} onChange={(e) => set('keyMessages', lines(e.target.value))} /></label>
        <label>KPIs (one per line, <code>name: target</code>)
          <textarea rows={4} disabled={!canWrite} value={c.kpis.map((k) => (k.target ? `${k.name}: ${k.target}` : k.name)).join('\n')}
            onChange={(e) => set('kpis', lines(e.target.value).map((l) => { const [n, ...t] = l.split(':'); return { name: n.trim(), target: t.join(':').trim() || undefined } }))} />
        </label>
        <label>Hypotheses (one per line)
          <textarea rows={3} disabled={!canWrite} value={c.hypotheses.map((h) => h.statement).join('\n')}
            onChange={(e) => set('hypotheses', lines(e.target.value).map((s) => c.hypotheses.find((h) => h.statement === s) ?? { statement: s, status: 'Open' }))} />
        </label>
        <label>Assumptions (one per line)<textarea rows={3} disabled={!canWrite} value={c.assumptions.join('\n')} onChange={(e) => set('assumptions', lines(e.target.value))} /></label>
        <label>Risks (one per line)<textarea rows={3} disabled={!canWrite} value={c.risks.join('\n')} onChange={(e) => set('risks', lines(e.target.value))} /></label>
        <label>Notes<textarea rows={8} disabled={!canWrite} value={c.notes} onChange={(e) => set('notes', e.target.value)} /></label>
        {canWrite && (
          <div className="row">
            <label>What changed?<input value={note} onChange={(e) => setNote(e.target.value)} /></label>
            <button type="submit" disabled={save.isPending}>Save as v{d.currentVersion + 1}</button>
          </div>
        )}
        {save.error instanceof ApiError && <div className="error" role="alert">{save.error.message}{save.error.violations.length > 0 && <ul>{save.error.violations.map((v) => <li key={v}>{v}</li>)}</ul>}</div>}
      </form>
      <p className="muted">History: {d.versions.map((v) => `v${v.number}${v.sourceDraftId ? ' (from AI draft)' : ''}${v.note ? ` — ${v.note}` : ''}`).join(' · ')}</p>
    </>
  )
}
