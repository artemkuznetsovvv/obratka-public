import { http } from './http'

// ----- Types (camelCase, как отдаёт Web API) -----

export interface TelegramStatus {
  linked: boolean
  botUsername: string
  configured: boolean
}

export interface TelegramLink {
  deepLink: string
  expiresAt: string
}

// ----- API -----

export const notificationsApi = {
  // Статус привязки Telegram текущего пользователя + конфигурация бота.
  telegramStatus: () =>
    http.get<TelegramStatus>('/api/notifications/telegram/status').then((r) => r.data),

  // Генерирует одноразовый deep-link t.me/<bot>?start=<token> для привязки.
  telegramLink: () =>
    http.post<TelegramLink>('/api/notifications/telegram/link').then((r) => r.data),

  // Отвязать Telegram от аккаунта.
  telegramUnlink: () =>
    http.post<void>('/api/notifications/telegram/unlink').then(() => undefined),
}
