import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type Approval } from '../api.ts'

export function ApprovalsPage() {
  const [status, setStatus] = useState('Pending')
  const qc = useQueryClient()
  const list = useQuery({ queryKey: ['approvals', status], queryFn: () => api.get<Approval[]>(`/api/v1/approvals?status=${status}`) })
  const decide = useMutation({
    mutationFn: (v: { id: string; decision: string; comment: string }) => api.post(`/api/v1/approvals/${v.id}/decision`, { decision: v.decision, comment: v.comment || null }),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['approvals'] }); void qc.invalidateQueries({ queryKey: ['versions'] }); void qc.invalidateQueries({ queryKey: ['product'] }); void qc.invalidateQueries({ queryKey: ['products'] }) },
  })
  const [comments, setComments] = useState<Record<string, string>>({})

  return (
    <>
      <h1>Approvals</h1>
      <div className="tabs">
        {['Pending', 'Approved', 'Rejected', 'ChangesRequested'].map((s) => <button key={s} className={s === status ? 'active link' : 'link'} onClick={() => setStatus(s)}>{s}</button>)}
      </div>
      {list.data?.length === 0 && <p className="muted">Nothing here.</p>}
      {decide.error instanceof ApiError && <p className="error" role="alert">{decide.error.message}</p>}
      {list.data?.map((a) => (
        <div className="card" key={a.id}>
          <strong>{a.title}</strong> <span className="muted">· {a.subjectType} v{a.subjectVersion} · requested {new Date(a.requestedAt).toLocaleString()}</span>
          {a.productId && <> · <Link to={`/products/${a.productId}/profile`}>open product</Link></>}
          {a.decisionComment && <p className="muted">“{a.decisionComment}”</p>}
          {a.canDecide && (
            <div className="row">
              <input placeholder="Comment (required to reject / request changes)" value={comments[a.id] ?? ''} onChange={(e) => setComments({ ...comments, [a.id]: e.target.value })} />
              <button onClick={() => decide.mutate({ id: a.id, decision: 'Approved', comment: comments[a.id] ?? '' })}>Approve</button>
              <button className="secondary" onClick={() => decide.mutate({ id: a.id, decision: 'ChangesRequested', comment: comments[a.id] ?? '' })}>Request changes</button>
              <button className="danger" onClick={() => decide.mutate({ id: a.id, decision: 'Rejected', comment: comments[a.id] ?? '' })}>Reject</button>
            </div>
          )}
          {a.status === 'Pending' && !a.canDecide && <p className="muted">You do not have permission to decide this.</p>}
        </div>
      ))}
    </>
  )
}
