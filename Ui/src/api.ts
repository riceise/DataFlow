import axios from 'axios'
import { errorMessages } from '@/models'

export const http = axios.create({ baseURL: '/api/v1', withCredentials: true })
let csrf = ''
let tokenRequest: Promise<string> | undefined

export type RuleDefinitionIssue = {
  code: string
  path: string
  message: string
  hint: string
  fieldName?: string | null
}

export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public code: string,
    public errors: Record<string, string[]> = {},
    public correlationId = '',
    public issues: RuleDefinitionIssue[] = [],
  ) {
    super(message)
  }
}

http.interceptors.request.use(async (config) => {
  if (!['get', 'head', 'options'].includes(config.method ?? 'get')) {
    if (!csrf) {
      tokenRequest ??= http
        .get<{ token: string }>('/auth/antiforgery')
        .then((r) => r.data.token)
        .finally(() => {
          tokenRequest = undefined
        })
      csrf = await tokenRequest
    }
    config.headers.set('X-CSRF-TOKEN', csrf)
  }
  return config
})
http.interceptors.response.use(
  (response) => {
    if (['/auth/login', '/auth/logout'].includes(response.config.url ?? '')) csrf = ''
    return response
  },
  async (error) => {
    if (axios.isCancel(error)) return Promise.reject(error)
    const response = axios.isAxiosError(error) ? error.response : undefined
    let problem = response?.data ?? {}
    if (typeof Blob !== 'undefined' && problem instanceof Blob) {
      try {
        problem = JSON.parse(await problem.text())
      } catch {
        problem = {}
      }
    }
    const code = typeof problem?.code === 'string' ? problem.code : ''
    const mapped = code ? errorMessages[code] : undefined
    const message =
      response?.status === 401
        ? 'Необходимо войти в систему.'
        : mapped
          ? mapped
          : response?.status === 404
            ? 'Данные не найдены.'
            : !response
              ? 'Нет связи с сервером. Повторите запрос.'
              : (problem?.title ?? 'Не удалось выполнить запрос.')
    return Promise.reject(
      new ApiError(
        message,
        response?.status ?? 0,
        code,
        problem?.errors ?? {},
        response?.headers['x-correlation-id'] ?? '',
        Array.isArray(problem?.issues) ? problem.issues : [],
      ),
    )
  },
)

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = Object.fromEntries(new Headers(init.headers).entries())
  if (init.body && !(init.body instanceof FormData)) headers['Content-Type'] = 'application/json'
  return (
    await http.request<T>({
      url: path,
      method: init.method ?? 'GET',
      data: init.body,
      headers,
      signal: init.signal ?? undefined,
    })
  ).data
}
