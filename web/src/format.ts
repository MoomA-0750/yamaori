// 表示の形（時刻はこのブラウザのタイムゾーンで出す）。

const two = (n: number) => String(n).padStart(2, '0')

export function time(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  return `${two(d.getHours())}:${two(d.getMinutes())}`
}

export function dateTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()} ${time(iso)}`
}

const week = ['日', '月', '火', '水', '木', '金', '土']

/** 「2026-11-08」→「11月8日（日）」。年が今年でなければ年も付ける。 */
export function day(date: string): string {
  const [y, m, d] = date.split('-').map(Number)
  const w = week[new Date(y, m - 1, d).getDay()]
  const year = y !== new Date().getFullYear() ? `${y}年` : ''
  return `${year}${m}月${d}日（${w}）`
}

/** その時刻のローカルの日付（yyyy-MM-dd）。 */
export function localDate(iso: string): string {
  const d = new Date(iso)
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}`
}

export function minutes(total: number): string {
  if (total < 1) return '1分未満'
  const h = Math.floor(total / 60)
  const m = Math.round(total % 60)
  if (h === 0) return `${m}分`
  return m === 0 ? `${h}時間` : `${h}時間${m}分`
}

export function between(from: string, to: string | null | undefined): string {
  const end = to ? new Date(to) : new Date()
  return minutes((end.getTime() - new Date(from).getTime()) / 60000)
}

const access: Record<string, string> = {
  public: 'パブリック',
  friends_plus: 'フレンド+',
  friends: 'フレンド',
  invite_plus: '招待+',
  invite: '招待',
  group_public: 'グループ公開',
  group_plus: 'グループ+',
  group: 'グループ',
}
export const accessLabel = (a: string) => access[a] ?? a

const leftReasons: Record<string, string> = {
  left: '',
  moved: '移動の開始で区切った',
  quit: '終了',
  log_end: 'ログの終わりで区切った',
  estimated: '推定',
}
export const leftLabel = (r: string | null | undefined) => (r ? leftReasons[r] ?? r : '')

const notificationTypes: Record<string, string> = {
  friendRequest: 'フレンド申請',
  invite: '招待',
  requestInvite: 'リクエスト招待',
  inviteResponse: '招待への返事',
  requestInviteResponse: 'リクエスト招待への返事',
  group: 'グループのお知らせ',
  localNotifs: 'お知らせ',
}
const directions: Record<string, string> = { received: '受けた', sent: '送った', accepted: '承諾した', answered: '答えた' }
export const notificationLabel = (type?: string | null, direction?: string | null) =>
  `${notificationTypes[type ?? ''] ?? type ?? ''}${direction ? `（${directions[direction] ?? direction}）` : ''}`

export const worldName = (v: { worldName?: string | null; worldId: string }) => v.worldName || v.worldId

/** リンクにしてよいアドレスなら返す（http・https だけ。javascript: などはリンクにしない）。 */
export function safeHref(url: string | null | undefined): string | null {
  if (!url) return null
  try {
    const u = new URL(url)
    return u.protocol === 'http:' || u.protocol === 'https:' ? u.href : null
  } catch {
    return null
  }
}

/** ワールドの一覧の名前（Yamaori で保存した一覧は鍵 saved で持ち、画面では「保存」と出す）。 */
export const listLabel = (name: string) => (name === 'saved' ? '保存' : name)

/** フレンドかどうかの表示（分からなければ null）。 */
export const friendLabel = (f: string | null | undefined) =>
  f === 'friend' ? 'フレンド' : f === 'former' ? '前はフレンド' : f === 'requested' ? '申請あり' : null
