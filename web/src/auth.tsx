import { createContext, useContext, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type Me } from './api'

interface AuthState {
  me: Me | null
  loading: boolean
  can: (permission: string) => boolean
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

const Ctx = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient()
  const q = useQuery({
    queryKey: ['me'],
    retry: false,
    queryFn: async () => {
      try { return await api.get<Me>('/api/v1/auth/me') } catch (e) {
        if (e instanceof ApiError && e.status === 401) return null
        throw e
      }
    },
  })
  const me = q.data ?? null
  const value: AuthState = {
    me,
    loading: q.isLoading,
    can: (p) => !!me?.permissions.includes(p),
    login: async (email, password) => { await api.post('/api/v1/auth/login', { email, password }); await qc.invalidateQueries({ queryKey: ['me'] }) },
    logout: async () => { await api.post('/api/v1/auth/logout'); qc.clear(); await qc.invalidateQueries({ queryKey: ['me'] }) },
  }
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useAuth() {
  const v = useContext(Ctx)
  if (!v) throw new Error('AuthProvider missing')
  return v
}
