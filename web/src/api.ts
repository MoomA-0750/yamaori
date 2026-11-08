// Yamaori の API を呼ぶ。型は OpenAPI の定義（openapi.json）から作った schema.d.ts による（npm run types で作り直す）。
import type { components } from './schema'

type S = components['schemas']
export type VisitSummary = S['VisitSummary']
export type VisitDetail = S['VisitDetail']
export type DaySummary = S['DaySummary']
export type DayView = S['DayView']
export type WorldSummary = S['WorldSummary']
export type WorldDetail = S['WorldDetail']
export type PersonSummary = S['PersonSummary']
export type PersonDetail = S['PersonDetail']
export type AccountView = S['AccountView']
export type DeviceView = S['DeviceView']
/** トークンの範囲。full は設定以外の全部、extension は拡張機能とスマホの保存、sync は子の端末。 */
export type TokenScope = 'full' | 'extension' | 'sync'
export type Note = S['Note']
export type EventView = S['EventView']
export type AuthState = S['AuthState']
export type SettingsView = S['SettingsView']
export type ApiToken = S['ApiToken']
export type PhotoView = S['PhotoView']
export type PhotoSettingsView = S['PhotoSettingsView']
export type Page<T> = { items: T[]; total: number }
export type TargetType = 'visit' | 'world' | 'person' | 'day'

/** ログインが要るときに投げる（画面はログインへ切り替える）。 */
export class NeedLogin extends Error {}

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

let onNeedLogin: () => void = () => {}
export function setNeedLoginHandler(f: () => void) { onNeedLogin = f }

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method,
    headers: body === undefined ? {} : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
  })
  if (res.status === 401) {
    onNeedLogin()
    throw new NeedLogin()
  }
  if (!res.ok) {
    let msg = `${res.status}`
    try {
      const t = await res.text()
      if (t) msg = t.replace(/^"|"$/g, '')
    } catch { /* そのまま */ }
    throw new ApiError(res.status, msg)
  }
  if (res.status === 204 || res.status === 202) return undefined as T
  return (await res.json()) as T
}

const q = (params: Record<string, string | number | undefined | null>) => {
  const s = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') s.set(k, String(v))
  const t = s.toString()
  return t ? `?${t}` : ''
}

export const api = {
  status: () => call<S['Status']>('GET', '/api/status'),
  days: () => call<DaySummary[]>('GET', '/api/days'),
  day: (date: string) => call<DayView>('GET', `/api/days/${date}`),
  visits: (p: { world?: string; person?: string; limit?: number; offset?: number } = {}) => call<Page<VisitSummary>>('GET', '/api/visits' + q(p)),
  visit: (id: string) => call<VisitDetail>('GET', `/api/visits/${encodeURIComponent(id)}`),
  worlds: (p: { q?: string; sort?: string; limit?: number; offset?: number; list?: string } = {}) => call<Page<WorldSummary>>('GET', '/api/worlds' + q(p)),
  world: (id: string) => call<WorldDetail>('GET', `/api/worlds/${encodeURIComponent(id)}`),
  people: (p: { q?: string; sort?: string; limit?: number; offset?: number; kind?: string } = {}) => call<Page<PersonSummary>>('GET', '/api/people' + q(p)),
  person: (id: string) => call<PersonDetail>('GET', `/api/people/${encodeURIComponent(id)}`),
  forgetPerson: (id: string, label: string | null) => call<S['ForgetResult']>('POST', `/api/people/${encodeURIComponent(id)}/forget`, { label }),
  forgotten: () => call<S['ForgottenPerson'][]>('GET', '/api/people/forgotten'),
  unforget: (key: string) => call<void>('DELETE', `/api/people/forgotten/${encodeURIComponent(key)}`),
  photos: (p: { visit?: string; world?: string; from?: string; to?: string; limit?: number; offset?: number } = {}) => call<Page<PhotoView>>('GET', '/api/photos' + q(p)),
  thumb: (id: string, size = 320) => `/api/photos/${id}/thumb?size=${size}`,
  photoFile: (id: string) => `/api/photos/${id}/file`,
  saveWorld: (b: { url?: string | null; text?: string | null; worldId?: string | null; name?: string | null; list?: string | null }) =>
    call<{ worldId: string; list: string }>('POST', '/api/worlds/save', b),
  worldLists: () => call<{ name: string; count: number }[]>('GET', '/api/worlds/lists'),
  accounts: () => call<AccountView[]>('GET', '/api/accounts'),
  devices: () => call<DeviceView[]>('GET', '/api/devices'),
  addNote: (targetType: TargetType, targetId: string, body: string) => call<Note>('POST', '/api/notes', { targetType, targetId, body }),
  updateNote: (id: string, body: string) => call<Note>('PUT', `/api/notes/${id}`, { body }),
  deleteNote: (id: string) => call<void>('DELETE', `/api/notes/${id}`),
  authState: () => call<AuthState>('GET', '/api/auth/state'),
  login: (password: string) => call<void>('POST', '/api/auth/login', { password }),
  logout: () => call<void>('POST', '/api/auth/logout'),
  setPassword: (current: string | null, next: string) => call<void>('POST', '/api/auth/password', { current, new: next }),
  tokens: () => call<ApiToken[]>('GET', '/api/auth/tokens'),
  createToken: (name: string, scope: TokenScope = 'full') => call<S['TokenCreated']>('POST', '/api/auth/tokens', { name, scope }),
  deleteToken: (id: string) => call<void>('DELETE', `/api/auth/tokens/${id}`),
  settings: () => call<SettingsView>('GET', '/api/settings/'),
  saveSettings: (s: Partial<S['SettingsInput']>) => call<S['SettingsResult']>('PUT', '/api/settings/', s),
  photoSettings: () => call<PhotoSettingsView>('GET', '/api/settings/photos/'),
  savePhotoSettings: (s: Partial<S['PhotoSettingsInput']>) => call<PhotoSettingsView>('PUT', '/api/settings/photos/', s),
  testPhotoTarget: (s: Partial<S['PhotoSettingsInput']>) => call<S['ConnectionTest']>('POST', '/api/settings/photos/test', s),
  syncSettings: () => call<S['SyncSettingsView']>('GET', '/api/settings/sync/'),
  saveSyncSettings: (s: Partial<S['SyncSettingsInput']>) => call<S['SyncSettingsView']>('PUT', '/api/settings/sync/', s),
  testParent: (s: Partial<S['SyncSettingsInput']>) => call<S['ConnectionTest']>('POST', '/api/settings/sync/test', s),
  restart: () => call<void>('POST', '/api/settings/restart'),
}
