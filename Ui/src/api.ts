let csrf = ''

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const method = init.method ?? 'GET'
  if (method !== 'GET' && !csrf) {
    const response = await fetch('/api/v1/auth/antiforgery', { credentials: 'same-origin' })
    csrf = ((await response.json()) as { token: string }).token
  }
  const headers = new Headers(init.headers)
  if (method !== 'GET') headers.set('X-CSRF-TOKEN', csrf)
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  const response = await fetch(`/api/v1${path}`, { ...init, headers, credentials: 'same-origin' })
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ title: response.statusText }))
    throw new Error(problem.errors ? JSON.stringify(problem.errors) : problem.title ?? `HTTP ${response.status}`)
  }
  if (path === '/auth/login' || path === '/auth/logout') csrf = ''
  return response.status === 204 ? (undefined as T) : (await response.json()) as T
}
