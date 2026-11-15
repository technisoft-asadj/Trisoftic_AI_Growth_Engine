import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type AuditItem, type JobItem, type PauseItem } from '../api.ts'
import { useAuth } from '../auth.tsx'

export function AdminPage() {
  const { can } = useAuth()
  return (
    <>
      <h1>Admin</h1>
      {can('governance.pause') && <PauseSwitches />}
      {can('jobs.manage') && <Jobs />}
      {can('audit.read') && <Audit />}
    </>
  )
}

function PauseSwitches() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['pause'], queryFn: () => api.get<PauseItem[]>('/api/v1/governance/pause') })
  const set = useMutation({
    mutationFn: (v: { scope: string; isPaused: boolean }) => api.put(`/api/v1/governance/pause/${v.scope}`, { isPaused: v.isPaused, reason: v.isPaused ? 'Paused from admin console' : null }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['pause'] }),
  })
  return (
    <div className="card">
      <h2>Emergency stop</h2>
      <p className="muted">Pausing a scope stops workers from performing that kind of external action until resumed.</p>
      <div className="row">
        {q.data?.map((s) => (
          <button key={s.scope} className={s.isPaused ? 'danger' : 'secondary'} onClick={() => set.mutate({ scope: s.scope, isPaused: !s.isPaused })}>
            {s.scope}: {s.isPaused ? 'PAUSED — resume' : 'running — pause'}
          </button>
        ))}
      </div>
    </div>
  )
}

function Jobs() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['jobs'], queryFn: () => api.get<JobItem[]>('/api/v1/jobs?take=50'), refetchInterval: 10_000 })
  const act = useMutation({
    mutationFn: (v: { id: string; verb: 'retry' | 'cancel' }) => api.post(`/api/v1/jobs/${v.id}/${v.verb}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['jobs'] }),
  })
  return (
    <div className="card">
      <h2>Background jobs</h2>
      {q.data?.length === 0 && <p className="muted">No jobs.</p>}
      <table>
        <thead><tr><th>Type</th><th>Status</th><th>Attempts</th><th>Error</th><th /></tr></thead>
        <tbody>
          {q.data?.map((j) => (
            <tr key={j.id}>
              <td>{j.type}</td><td><span className={j.status === 'DeadLettered' ? 'badge warn' : 'badge'}>{j.status}</span></td>
              <td>{j.attempts}/{j.maxAttempts}</td><td className="muted">{j.lastError}</td>
              <td>
                {(j.status === 'DeadLettered' || j.status === 'Cancelled') && <button className="link" onClick={() => act.mutate({ id: j.id, verb: 'retry' })}>replay</button>}
                {(j.status === 'Queued' || j.status === 'DeadLettered') && <button className="link" onClick={() => act.mutate({ id: j.id, verb: 'cancel' })}>cancel</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Audit() {
  const q = useQuery({ queryKey: ['audit'], queryFn: () => api.get<{ items: AuditItem[] }>('/api/v1/governance/audit?take=50') })
  return (
    <div className="card">
      <h2>Audit log</h2>
      <table>
        <thead><tr><th>When</th><th>Who</th><th>Action</th><th>Subject</th><th>Summary</th></tr></thead>
        <tbody>
          {q.data?.items.map((a) => (
            <tr key={a.id}><td>{new Date(a.occurredAt).toLocaleString()}</td><td>{a.actorEmail ?? 'system'}</td><td><code>{a.action}</code></td><td>{a.subjectType}</td><td className="muted">{a.summary}</td></tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
