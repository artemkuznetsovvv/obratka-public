import axios from 'axios'

/**
 * Превращает любую ошибку (axios / generic Error / unknown) в строку для UI.
 * Для axios-ошибок предпочитает поле `detail` из ProblemDetails-ответа сервера
 * (RFC 7807) — туда Web API кладёт человеческий текст. Если detail нет,
 * пробует `title`, потом fallback на `err.message` (для не-axios — Error.message).
 *
 * Без этого хелпера axios прокидывает в UI бесполезные строки типа
 * "Request failed with status code 500" вместо полезного текста из тела ответа.
 */
export function describeApiError(err: unknown, fallback = 'Неизвестная ошибка'): string {
  if (axios.isAxiosError(err)) {
    const data = err.response?.data
    if (data && typeof data === 'object') {
      const detail = (data as { detail?: unknown }).detail
      if (typeof detail === 'string' && detail.trim()) return detail
      const title = (data as { title?: unknown }).title
      if (typeof title === 'string' && title.trim()) return title
      const error = (data as { error?: unknown }).error
      if (typeof error === 'string' && error.trim()) return error
    }
    if (err.message) return err.message
  }
  if (err instanceof Error && err.message) return err.message
  return fallback
}
