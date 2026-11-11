import axios from 'axios'

export const http = axios.create({ baseURL: '/api/v1', withCredentials: true })
let csrf = ''
let tokenRequest: Promise<string> | undefined

export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public code: string,
    public errors: Record<string, string[]> = {},
    public correlationId = '',
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
  (error) => {
    if (axios.isCancel(error)) return Promise.reject(error)
    const response = axios.isAxiosError(error) ? error.response : undefined
    const problem = response?.data ?? {}
    const message =
      response?.status === 401
        ? 'Необходимо войти в систему.'
        : response?.status === 404
          ? 'Данные не найдены.'
          : !response
            ? 'Нет связи с сервером. Повторите запрос.'
            : (problem.title ?? 'Не удалось выполнить запрос.')
    return Promise.reject(
      new ApiError(
        message,
        response?.status ?? 0,
        problem.code ?? '',
        problem.errors ?? {},
        response?.headers['x-correlation-id'] ?? '',
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
