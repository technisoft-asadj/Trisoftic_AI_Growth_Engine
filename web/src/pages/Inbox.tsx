import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../api.ts'

export interface WorkSummary { openTasks: number; overdueTasks: number; unreadNotifications: number }
interface TaskRow { id: string; title: string; notes?: string; dueAt?: string; completedAt?: string; overdue: boolean; subjectType?: string; subjectId?: string }
interface Note { id: string; title: string; body?: string; link?: string; createdAt: string; readAt?: string }

const err = (e: unknown) => (e instanceof ApiError ? e.message : e ? 'Something went wrong' : null)
const subjectLink = (t: TaskRow) => (t.subjectType === 'lead' ? `/crm/leads/${t.subjectId}` : t.subjectType === 'opportunity' ? `/crm/deals/${t.subjectId}` : null)

export function useWorkSummary(enabled: boolean) {
  return useQuery({ queryKey: ['work-summary'], enabled, queryFn: () => api.get<WorkSummary>('/api/v1/me/work-summary'), refetchInterval: 60_000 })
}

export function InboxPage() {
  const qc = useQueryClient()
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['tasks'] }); void qc.invalidateQueries({ queryKey: ['notes'] }); void qc.invalidateQueries({ queryKey: ['work-summary'] }) }
  const [showDone, setShowDone] = useState(false)
  const tasks = useQuery({ queryKey: ['tasks', showDone], queryFn: () => api.get<TaskRow[]>(`/api/v1/tasks?includeDone=${showDone}`) })
  const notes = useQuery({ queryKey: ['notes'], queryFn: () => api.get<Note[]>('/api/v1/notifications') })
  const [title, setTitle] = useState(''); const [due, setDue] = useState('')
  const add = useMutation({
    mutationFn: () => api.post('/api/v1/tasks', { title, dueAt: due ? new Date(due).toISOString() : null }),
    onSuccess: () => { setTitle(''); setDue(''); refresh() },
  })
  const done = useMutation({ mutationFn: (id: string) => api.post(`/api/v1/tasks/${id}/complete`), onSuccess: refresh })
  const read = useMutation({ mutationFn: (id: string) => api.post(`/api/v1/notifications/${id}/read`), onSuccess: refresh })
  const readAll = useMutation({ mutationFn: () => api.post('/api/v1/notifications/read-all'), onSuccess: refresh })

  return (
    <>
      <h1>Inbox</h1>
      <div className="card">
        <h2>My tasks</h2>
        <div className="row">
          <label>New task<input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={300} /></label>
          <label>Due<input type="datetime-local" value={due} onChange={(e) => setDue(e.target.value)} /></label>
          <button disabled={!title.trim() || add.isPending} onClick={() => add.mutate()}>Add</button>
          <label className="inline"><input type="checkbox" checked={showDone} onChange={(e) => setShowDone(e.target.checked)} /> Show completed</label>
        </div>
        {err(add.error ?? done.error ?? tasks.error) && <p className="error" role="alert">{err(add.error ?? done.error ?? tasks.error)}</p>}
        {tasks.data?.length === 0 && <p className="muted">Nothing to do.</p>}
        {!!tasks.data?.length && (
          <table>
            <tbody>{tasks.data.map((t) => (
              <tr key={t.id}>
                <td>{t.completedAt ? <s>{t.title}</s> : t.title}{subjectLink(t) && <> · <Link to={subjectLink(t)!}>open</Link></>}</td>
                <td>{t.dueAt ? <span className={t.overdue ? 'badge warn' : 'muted'}>{t.overdue ? 'Overdue · ' : ''}{new Date(t.dueAt).toLocaleString()}</span> : <span className="muted">No due date</span>}</td>
                <td>{!t.completedAt && <button className="secondary" onClick={() => done.mutate(t.id)}>Done</button>}</td>
              </tr>
            ))}</tbody>
          </table>
        )}
      </div>
      <div className="card">
        <h2>Notifications</h2>
        <button className="link" onClick={() => readAll.mutate()}>Mark all as read</button>
        {notes.data?.length === 0 && <p className="muted">No notifications.</p>}
        {!!notes.data?.length && (
          <table>
            <tbody>{notes.data.map((n) => (
              <tr key={n.id} style={{ fontWeight: n.readAt ? 400 : 700 }}>
                <td>{n.link ? <Link to={n.link} onClick={() => read.mutate(n.id)}>{n.title}</Link> : n.title}</td>
                <td className="muted">{new Date(n.createdAt).toLocaleString()}</td>
                <td>{!n.readAt && <button className="link" onClick={() => read.mutate(n.id)}>Mark read</button>}</td>
              </tr>
            ))}</tbody>
          </table>
        )}
      </div>
    </>
  )
}
