// Module-level access token cache so the axios interceptor can read/write
// it without going through React. AuthProvider mirrors this into React state.

let accessToken: string | null = null
let logoutHandler: (() => void) | null = null

export const tokenStore = {
  get(): string | null {
    return accessToken
  },
  set(token: string | null) {
    accessToken = token
  },
  registerLogoutHandler(fn: () => void) {
    logoutHandler = fn
  },
  triggerLogout() {
    logoutHandler?.()
  },
}
