// Thin fetch wrapper: base URL, session cookie, JSON, and one error shape for every failure.

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

type AuthHandler = () => void

let onUnauthorized: AuthHandler = () => {}
let onActivity: AuthHandler = () => {}
let onWrongRole: AuthHandler = () => {}

/**
 * Wired by AuthProvider, so the client never imports React state. `activity` runs after every
 * request the API answered with the session, because each one restarted its idle timer.
 * `wrongRole` runs on a 403 `forbidden`: RequireRole keeps each role on its own pages, so the cookie
 * must now belong to someone else, after another account signed in elsewhere in this browser.
 */
export function configureAuth(unauthorized: AuthHandler, activity: AuthHandler = () => {}, wrongRole: AuthHandler = () => {}) {
  onUnauthorized = unauthorized
  onActivity = activity
  onWrongRole = wrongRole
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PATCH'
  body?: unknown
  headers?: Record<string, string>
  signal?: AbortSignal
  /**
   * For the sign-in and sign-out calls themselves: login returns 401 for bad credentials, which
   * must not trigger a sign-out, and neither call is activity in an existing session.
   */
  skipAuthRedirect?: boolean
}

export interface ApiResponse<T> {
  data: T
  headers: Headers
  status: number
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<ApiResponse<T>> {
  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'

  let response: Response
  try {
    response = await fetch(`${API_BASE}${path}`, {
      method: options.method ?? 'GET',
      headers,
      // The session is an httpOnly cookie on the API's origin; JavaScript never sees it.
      credentials: 'include',
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    })
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') throw err
    throw new ApiError(0, messageFromProblem(null, 0), 'network_error')
  }

  const text = await response.text()
  const body = text ? safeParse(text) : null

  // 503 is the session store being down: the session was not touched, so nothing restarted.
  if (!options.skipAuthRedirect && response.status !== 401 && response.status !== 503) onActivity()

  if (!response.ok) {
    if (response.status === 401 && !options.skipAuthRedirect) onUnauthorized()
    const problem = body as ProblemBody | null
    if (response.status === 403 && problem?.code === 'forbidden' && !options.skipAuthRedirect) onWrongRole()
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
