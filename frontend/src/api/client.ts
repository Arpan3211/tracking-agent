const API_BASE = '/api/v1'

function getCookie(name: string): string | null {
  const match = document.cookie.match(new RegExp('(?:^|; )' + name + '=([^;]*)'))
  return match ? decodeURIComponent(match[1]) : null
}

export class ApiError extends Error {
  status: number
  body: unknown

  constructor(status: number, body: unknown, message: string) {
    super(message)
    this.status = status
    this.body = body
  }
}

let refreshPromise: Promise<boolean> | null = null

async function tryRefresh(): Promise<boolean> {
  // Coalesce concurrent 401s (e.g. several widgets fetching at once when the
  // access token just expired) into a single refresh call instead of one
  // refresh request per failed request.
  if (!refreshPromise) {
    refreshPromise = fetch(`${API_BASE}/auth/refresh`, { method: 'POST', credentials: 'include' })
      .then((res) => res.ok)
      .finally(() => {
        refreshPromise = null
      })
  }
  return refreshPromise
}

interface RequestOptions {
  method?: string
  body?: unknown
  isRetry?: boolean
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, isRetry = false } = options
  const isMutating = method !== 'GET'

  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (isMutating) {
    // Double-submit CSRF: the csrf_token cookie is deliberately NOT
    // httpOnly (see backend app/core/csrf.py) specifically so this can read
    // it and echo it back as a header.
    const csrfToken = getCookie('csrf_token')
    if (csrfToken) headers['X-CSRF-Token'] = csrfToken
  }

  const response = await fetch(`${API_BASE}${path}`, {
    method,
    headers,
    credentials: 'include',
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })

  if (response.status === 401 && !isRetry && path !== '/auth/login' && path !== '/auth/refresh') {
    const refreshed = await tryRefresh()
    if (refreshed) {
      return request<T>(path, { ...options, isRetry: true })
    }
  }

  if (!response.ok) {
    let errorBody: unknown = null
    try {
      errorBody = await response.json()
    } catch {
      // no JSON body on this error response
    }
    const message = (errorBody as { detail?: string } | null)?.detail ?? `Request failed: ${response.status}`
    throw new ApiError(response.status, errorBody, message)
  }

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) => request<T>(path, { method: 'POST', body }),
  patch: <T>(path: string, body?: unknown) => request<T>(path, { method: 'PATCH', body }),
}

export function buildQuery(
  params: Record<string, string | number | boolean | string[] | undefined | null>,
): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '') continue
    if (Array.isArray(value)) {
      for (const v of value) search.append(key, v)
    } else {
      search.set(key, String(value))
    }
  }
  const qs = search.toString()
  return qs ? `?${qs}` : ''
}

export function exportUrl(deviceId: string, format: 'csv' | 'xlsx', from?: string, to?: string): string {
  return `${API_BASE}/reports/export${buildQuery({ device_id: deviceId, format, from, to })}`
}

export function websocketUrl(path: string): string {
  const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${protocol}//${window.location.host}${API_BASE}${path}`
}
