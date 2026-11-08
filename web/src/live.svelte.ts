// 今の出来事（/api/stream の SSE）を受けて、「いま」の様子を持つ。
// 画面は live.version を見て、変わったら読み直す。
import { api } from './api'

type Current = { visitId?: string; worldId: string; worldName?: string; joinedAt: string; people: Record<string, string> }

export const live = $state({
  connected: false,
  current: null as Current | null,
  version: 0,
})

let source: EventSource | null = null
let pendingName: string | undefined

function bump() { live.version++ }

/** 今いる訪問を、記録から拾う（開いたときと、つなぎ直したとき）。 */
async function loadCurrent() {
  try {
    const page = await api.visits({ limit: 1 })
    const v = page.items[0]
    if (!v || v.leftAt) { live.current = null; return }
    const d = await api.visit(v.id)
    const people: Record<string, string> = {}
    for (const p of d.people) if (!p.leftAt) people[p.userId ?? `name:${p.name}`] = p.name
    live.current = { visitId: v.id, worldId: v.worldId, worldName: v.worldName ?? undefined, joinedAt: v.joinedAt, people }
  } catch { /* ログインの前など */ }
}

export function startLive() {
  if (source) return
  source = new EventSource('/api/stream')
  source.addEventListener('hello', () => { live.connected = true; loadCurrent() })
  source.onerror = () => { live.connected = false }
  const on = (kind: string, f: (d: Record<string, string | null>, at: string) => void) =>
    source!.addEventListener(kind, (ev) => {
      const e = JSON.parse((ev as MessageEvent).data) as { at: string; data: Record<string, string | null> }
      f(e.data, e.at)
      bump()
    })
  on('world_name', (d) => { pendingName = d.world_name ?? undefined; if (live.current && !live.current.worldName) live.current.worldName = pendingName })
  on('joined', (d, at) => {
    live.current = { worldId: d.world_id ?? '', worldName: pendingName, joinedAt: at, people: {} }
    pendingName = undefined
    setTimeout(loadCurrent, 3000) // 訪問の ID は記録に入ってから拾う
  })
  on('left', () => { live.current = null })
  on('quit', () => { live.current = null })
  on('player_joined', (d) => { if (live.current) live.current.people[d.user_id ?? `name:${d.name}`] = d.name ?? '' })
  on('player_left', (d) => { if (live.current) delete live.current.people[d.user_id ?? `name:${d.name}`] })
  on('video', () => {})
  on('screenshot', () => {})
  on('notification', () => {})
}

export function stopLive() {
  source?.close()
  source = null
  live.connected = false
}
