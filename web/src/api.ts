export class ApiError extends Error {
  status: number
  violations: string[]
  constructor(status: number, message: string, violations: string[] = []) {
    super(message)
    this.status = status
    this.violations = violations
  }
}

type Json = Record<string, unknown> | unknown[]

async function request<T>(method: string, path: string, body?: Json | FormData): Promise<T> {
  const isForm = body instanceof FormData
  const res = await fetch(path, {
    method,
    credentials: 'same-origin',
    headers: {
      'X-Requested-With': 'trisoftic-web', // required by the API's CSRF guard on unsafe methods
      ...(body && !isForm ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? (isForm ? body : JSON.stringify(body)) : undefined,
  })
  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = text ? safeJson(text) : undefined
  if (!res.ok) {
    const d = data as { title?: string; errors?: Record<string, string[]> } | undefined
    const violations = d?.errors ? Object.values(d.errors).flat() : []
    throw new ApiError(res.status, d?.title ?? `Request failed (${res.status})`, violations)
  }
  return data as T
}

function safeJson(t: string): unknown {
  try { return JSON.parse(t) } catch { return undefined }
}

export const api = {
  get: <T>(p: string) => request<T>('GET', p),
  post: <T>(p: string, b?: Json) => request<T>('POST', p, b ?? ({} as Json)),
  put: <T>(p: string, b: Json) => request<T>('PUT', p, b),
  upload: <T>(p: string, f: FormData) => request<T>('POST', p, f),
}

// ---- types mirroring the API DTOs ----
export interface Me { id: string; email: string; displayName: string; roles: string[]; permissions: string[]; allProducts: boolean; productIds: string[] }
export interface Category { id: string; name: string; kind: string }
export interface Product { id: string; name: string; slug: string; categoryId: string; kind: string; lifecycle: string; summary?: string; isArchived: boolean; approvedProfileVersion?: number }
export interface Fact { id: string; category: string; text: string; evidence?: string; verified: boolean }
export interface Claim { id: string; text: string; status: 'Approved' | 'Prohibited'; evidence?: string }
export interface Market { country: string; languages: string[]; notes?: string }
export interface Profile {
  summary: string; facts: Fact[]; claims: Claim[]; markets: Market[]; platforms: string[]
  personas: { id: string; name: string; description?: string; painPoints: string[] }[]
  brandVoice: { tone?: string; dos: string[]; donts: string[] }
  links: { label: string; url: string }[]; competitors: { name: string; url?: string; notes?: string }[]
}
export interface ProfileVersion { id: string; number: number; status: string; changeNote?: string; decisionComment?: string; content: Profile }
export interface Approval { id: string; subjectType: string; subjectId: string; subjectVersion: number; title: string; status: string; requestedAt: string; decisionComment?: string; canDecide: boolean; productId?: string }
export interface AssetItem { id: string; name: string; kind: string; version: number; contentType: string; sizeBytes: number; source: string; rights: string; isSynthetic: boolean }
export interface AuditItem { id: string; occurredAt: string; actorEmail?: string; action: string; subjectType: string; summary?: string }
export interface JobItem { id: string; type: string; status: string; attempts: number; maxAttempts: number; lastError?: string; runAt: string }
export interface PauseItem { scope: string; isPaused: boolean; reason?: string }
export interface Grounded {
  draftId: string
  draft: { title: string; body: string; claims: { text: string; factRef?: string; assumption: boolean }[]; questions: string[] }
  profileVersion: number; provider: string; model: string; costUsd: number; attempts: number
}

