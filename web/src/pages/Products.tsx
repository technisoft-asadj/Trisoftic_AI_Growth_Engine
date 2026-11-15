import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, type Category, type Product } from '../api.ts'
import { useAuth } from '../auth.tsx'

export function ProductsPage() {
  const { can, me } = useAuth()
  const qc = useQueryClient()
  const products = useQuery({ queryKey: ['products'], queryFn: () => api.get<Product[]>('/api/v1/products') })
  const categories = useQuery({ queryKey: ['categories'], queryFn: () => api.get<Category[]>('/api/v1/product-categories') })
  const [name, setName] = useState('')
  const [categoryId, setCategoryId] = useState('')
  const create = useMutation({
    mutationFn: () => api.post<Product>('/api/v1/products', { name, categoryId }),
    onSuccess: () => { setName(''); void qc.invalidateQueries({ queryKey: ['products'] }) },
  })
  const canCreate = can('products.write') && me?.allProducts

  function submit(e: FormEvent) { e.preventDefault(); create.mutate() }

  return (
    <>
      <h1>Products</h1>
      {products.isLoading && <p className="muted">Loading…</p>}
      {products.error && <p className="error">Could not load products.</p>}
      {products.data?.length === 0 && <p className="muted">No products yet.{canCreate ? ' Create the first one below.' : ''}</p>}
      <div className="grid">
        {products.data?.map((p) => (
          <Link key={p.id} to={`/products/${p.id}`} className="card tile">
            <strong>{p.name}</strong>
            <span className="muted">{p.kind} · {p.lifecycle}</span>
            <span className={p.approvedProfileVersion ? 'badge ok' : 'badge warn'}>
              {p.approvedProfileVersion ? `Profile v${p.approvedProfileVersion} approved` : 'No approved profile'}
            </span>
          </Link>
        ))}
      </div>

      {canCreate && (
        <form className="card" onSubmit={submit}>
          <h2>New product</h2>
          <div className="row">
            <label>Name<input value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} /></label>
            <label>Category
              <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} required>
                <option value="">Select…</option>
                {categories.data?.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
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
