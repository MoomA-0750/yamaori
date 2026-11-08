// vrchat.com のページで動く（拡張機能の側）。hook.js が渡す応答を覚えておき、
// 既定ではボタンを押したときだけ、設定で有効にしていれば自動で、Yamaori へ送るよう裏（background.js）に頼む。
(() => {
  const worlds = new Map()        // wrld_… → ワールドの応答
  const friendNames = new Map()   // usr_… → 表示名
  let self = null                 // { id, friends: [usr_…] }
  let settings = { autoWorlds: false, autoFriends: false }
  let button = null
  let lastFriendsSentAt = 0

  // 設定を読み終える前に応答が届くことがあるので、自動で送るかの判断は読み終えてからにする
  const ready = chrome.storage.local.get({ autoWorlds: false, autoFriends: false }).then((s) => { settings = s; refresh() })
  chrome.storage.onChanged.addListener((c) => {
    for (const k of Object.keys(c)) if (k in settings) settings[k] = c[k].newValue
    refresh()
  })

  const isWorld = (x) => x && typeof x === 'object' && typeof x.id === 'string' && x.id.startsWith('wrld_')

  window.addEventListener('message', (ev) => {
    if (ev.source !== window || ev.data?.source !== 'yamaori-hook') return
    const { url, data } = ev.data
    const path = new URL(url, location.origin).pathname
    const fresh = []
    if (/\/api\/1\/worlds\/wrld_/.test(path) && isWorld(data)) fresh.push(data)
    else if (/\/api\/1\/worlds$/.test(path) && Array.isArray(data)) fresh.push(...data.filter(isWorld))
    else if (/\/api\/1\/auth\/user\/friends$/.test(path) && Array.isArray(data)) {
      for (const u of data) if (u?.id) friendNames.set(u.id, u.displayName)
    } else if (/\/api\/1\/auth\/user$/.test(path) && data?.id && Array.isArray(data.friends)) {
      self = { id: data.id, friends: data.friends }
      ready.then(maybeSendFriends)
    }
    if (fresh.length) {
      for (const w of fresh) worlds.set(w.id, w)
      chrome.runtime.sendMessage({ type: 'captured', worldIds: fresh.map((w) => w.id), worlds: fresh }).catch(() => {})
      ready.then(() => { if (settings.autoWorlds) save(fresh, true) })
    }
    refresh()
  })

  /** 今のページに関係するワールド（ワールドのページならそれ、一覧ならこれまでに見えた分）。 */
  function pageWorlds() {
    const m = location.pathname.match(/\/home\/world\/(wrld_[\w-]+)/)
    if (m) return worlds.has(m[1]) ? [worlds.get(m[1])] : []
    return [...worlds.values()]
  }

  async function save(list, quiet = false) {
    if (!list.length) return
    setLabel('保存しています…')
    const r = await chrome.runtime.sendMessage({ type: 'saveWorlds', worlds: list }).catch((e) => ({ ok: false, error: String(e) }))
    if (r?.ok) setLabel(quiet ? `自動で保存しました（${r.saved}）` : `保存しました（${r.saved}）`)
    else setLabel(`保存できませんでした: ${r?.error ?? ''}`, true)
  }

  function friendsPayload() {
    if (!self) return null
    return { accountId: self.id, friends: self.friends.map((id) => ({ id, displayName: friendNames.get(id) ?? null })) }
  }

  async function maybeSendFriends() {
    if (!settings.autoFriends || !self) return
    if (Date.now() - lastFriendsSentAt < 10 * 60 * 1000) return
    lastFriendsSentAt = Date.now()
    await chrome.runtime.sendMessage({ type: 'saveFriends', payload: friendsPayload() }).catch(() => {})
  }

  // ポップアップから「このページを保存」「フレンドの一覧を記録」
  chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
    if (msg.type === 'savePage') {
      const list = pageWorlds()
      save(list).then(() => reply({ ok: true, count: list.length }))
      return true
    }
    if (msg.type === 'saveFriendsNow') {
      const payload = friendsPayload()
      if (!payload) { reply({ ok: false, error: 'このページではまだ本人の情報が見えていない（vrchat.com のページを開き直す）' }); return false }
      chrome.runtime.sendMessage({ type: 'saveFriends', payload }).then(reply)
      return true
    }
    if (msg.type === 'pageState') {
      reply({ worlds: pageWorlds().length, friends: self ? self.friends.length : null })
      return false
    }
    return false
  })

  // ---- ページのボタン（既定。自動で送る設定なら出さない）
  function refresh() {
    const list = pageWorlds()
    if (settings.autoWorlds || list.length === 0) { button?.remove(); button = null; return }
    if (!document.body) return
    if (!button) {
      button = document.createElement('button')
      button.type = 'button'
      Object.assign(button.style, {
        position: 'fixed', right: '18px', bottom: '18px', zIndex: 2147483646, padding: '10px 16px', borderRadius: '999px',
        border: '1px solid #b5562d', background: '#fffdf8', color: '#b5562d', font: '600 14px system-ui, sans-serif',
        boxShadow: '0 4px 16px rgba(0,0,0,.25)', cursor: 'pointer',
      })
      button.addEventListener('click', () => save(pageWorlds()))
      document.body.appendChild(button)
    }
    if (!button.dataset.busy) button.textContent = list.length === 1 ? 'Yamaori に保存' : `Yamaori に保存（${list.length}）`
  }

  function setLabel(text, error = false) {
    if (!button) return
    button.dataset.busy = '1'
    button.textContent = text
    button.style.color = error ? '#b3261e' : '#b5562d'
    setTimeout(() => { if (button) { delete button.dataset.busy; button.style.color = '#b5562d'; refresh() } }, 2500)
  }

  // ワールドのページの移り変わり（vrchat.com は 1 枚のページの中で道を変える）
  let lastPath = location.pathname
  setInterval(() => { if (location.pathname !== lastPath) { lastPath = location.pathname; refresh() } }, 700)
  document.addEventListener('DOMContentLoaded', refresh)
})()
