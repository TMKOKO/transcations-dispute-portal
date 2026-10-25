// Thin fetch wrapper: base URL, bearer token, JSON, and one error shape for every failure.

export const API_BASE = (import.meta.env.VITE_API_BASE ?? 'http://localhost:8080').replace(/\/$/, '')

/** RFC 7807 body as the API returns it, plus its `code` extension and model-validation `errors`. */
interface ProblemBody {
  title?: string
  detail?: string
  status?: number
  code?: string
  errors?: Record<string, string[]>
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined
  /** Field-level validation messages keyed by camelCase field name (400 responses). */
  readonly fieldErrors: Record<string, string[]>

  constructor(status: number, message: string, code?: string, fieldErrors: Record<string, string[]> = {}) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.fieldErrors = fieldErrors
  }
}

/**
 * Turn a ProblemDetails body into a message a customer can read. Model-validation 400s carry an
 * `errors` dictionary and only a generic title, so flatten those rather than showing
 * "One or more validation errors occurred." and hiding the real reason.
 */
export function messageFromProblem(body: ProblemBody | null, status: number): string {
  if (body?.detail) return body.detail
  const fieldMessages = Object.values(body?.errors ?? {}).flat()
  if (fieldMessages.length > 0) return fieldMessages.join(' ')
  if (body?.title) return body.title
  if (status === 0) return 'We could not reach the server. Check your connection and try again.'
  return 'Something went wrong. Please try again.'
}

type TokenSource = () => string | null
type UnauthorizedHandler = () => void

let getToken: TokenSource = () => null
let onUnauthorized: UnauthorizedHandler = () => {}

/** Wired once by AuthProvider, so the client never imports React state. */
export function configureAuth(tokenSource: TokenSource, unauthorized: UnauthorizedHandler) {
  getToken = tokenSource
  onUnauthorized = unauthorized
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PATCH'
  body?: unknown
  headers?: Record<string, string>
  signal?: AbortSignal
  /** Login itself returns 401 for bad credentials; that must not trigger a sign-out. */
  skipAuthRedirect?: boolean
}

export interface ApiResponse<T> {
  data: T
  headers: Headers
  status: number
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<ApiResponse<T>> {
  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers }
  const token = getToken()
  if (token) headers.Authorization = `Bearer ${token}`
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'

  let response: Response
  try {
    response = await fetch(`${API_BASE}${path}`, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    })
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') throw err
    throw new ApiError(0, messageFromProblem(null, 0), 'network_error')
  }

  const text = await response.text()
  const body = text ? safeParse(text) : null

  if (!response.ok) {
    if (response.status === 401 && !options.skipAuthRedirect) onUnauthorized()
    const problem = body as ProblemBody | null
    throw new ApiError(response.status, messageFromProblem(problem, response.status), problem?.code, problem?.errors)
  }

  return { data: body as T, headers: response.headers, status: response.status }
}

export async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  return (await request<T>(path, { signal })).data
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return null
  }
}
