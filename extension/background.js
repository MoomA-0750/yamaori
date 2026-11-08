// 裏で動く。content.js から頼まれたものを Yamaori の API へ送る（トークンを付ける）。
import { getSettings, toWorldDetails } from './common.js'

async function yamaori(method, path, body) {
  const s = await getSettings()
  if (!s.token) throw new Error('設定でトークンを入れる')
  const res = await fetch(s.yamaoriUrl.replace(/\/$/, '') + path, {
    method,
    headers: { authorization: `Bearer ${s.token}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!res.ok) throw new Error(`Yamaori が ${res.status} を返した`)
  return res.status === 204 ? null : res.json()
}

async function saveWorlds(worlds) {
  let saved = 0
  for (const w of worlds) {
    const { id, body } = toWorldDetails(w)
    await yamaori('PUT', `/api/worlds/${encodeURIComponent(id)}/details`, body)
    saved++
  }
  return saved
}

// まとめて取る画面（bulk.html）が、開いたタブの取れたワールドを待つ。
// ページの読み込みが速いと、待ち始める前に知らせが届くので、タブごとに届いた分も覚えておく
const waiting = new Map() // tabId → resolve
const captured = new Map() // tabId → これまでに届いたワールド

chrome.tabs.onRemoved.addListener((tabId) => captured.delete(tabId))

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (msg.type === 'saveWorlds') {
    saveWorlds(msg.worlds).then((saved) => reply({ ok: true, saved })).catch((e) => reply({ ok: false, error: e.message }))
    return true
  }
  if (msg.type === 'saveFriends') {
    yamaori('POST', '/api/friends/snapshot', msg.payload).then((r) => reply({ ok: true, result: r })).catch((e) => reply({ ok: false, error: e.message }))
    return true
  }
  if (msg.type === 'captured' && sender.tab) {
    // タブごとに新しい 50 件まで、覚えるタブも 20 まで（ページから大量に送られても膨らまない）
    const prev = captured.get(sender.tab.id) ?? []
    captured.delete(sender.tab.id) // 入れ直して、新しいタブを後ろにする
    captured.set(sender.tab.id, [...prev, ...msg.worlds].slice(-50))
    while (captured.size > 20) captured.delete(captured.keys().next().value)
    waiting.get(sender.tab.id)?.(msg.worlds)
    return false
  }
  if (msg.type === 'yamaori') {
    yamaori(msg.method, msg.path, msg.body).then((r) => reply({ ok: true, result: r })).catch((e) => reply({ ok: false, error: e.message }))
    return true
  }
  if (msg.type === 'openAndCapture') {
    openAndCapture(msg.worldId, msg.timeoutMs ?? 20000).then((w) => reply({ ok: !!w, world: w })).catch((e) => reply({ ok: false, error: e.message }))
    return true
  }
  return false
})

/** ワールドのページを裏のタブで開き、ページが受け取ったワールドの情報を待って閉じる。 */
async function openAndCapture(worldId, timeoutMs) {
  const tab = await chrome.tabs.create({ url: `https://vrchat.com/home/world/${worldId}`, active: false })
  try {
    return await new Promise((resolve) => {
      const timer = setTimeout(() => { waiting.delete(tab.id); resolve(null) }, timeoutMs)
      const check = (worlds) => {
        const w = worlds.find((x) => x.id === worldId)
        if (!w) return
        clearTimeout(timer)
        waiting.delete(tab.id)
        resolve(w)
      }
      waiting.set(tab.id, check)
      check(captured.get(tab.id) ?? [])
    })
  } finally {
    captured.delete(tab.id)
    chrome.tabs.remove(tab.id).catch(() => {})
  }
}
