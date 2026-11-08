// ページの中（MAIN）で動く。vrchat.com のページが自分で受け取った応答を横から見て、拡張機能の側へ渡すだけ。
// 自分から vrchat.com へ通信はしない。見るのは次の応答だけ:
//   /api/1/worlds/wrld_…（ワールド 1 つ）、/api/1/worlds?…（ワールドの一覧）、/api/1/auth/user（本人。フレンドの ID の一覧を含む）、
//   /api/1/auth/user/friends?…（フレンドの名前を引くため）
(() => {
  const WATCH = /\/api\/1\/(worlds(\/wrld_[\w-]+)?|auth\/user(\/friends)?)(\?|$)/
  // ページへ流すのは要る項目だけ（本人の応答にはメールアドレスなども入るので、丸ごとは流さない）
  const trim = (url, data) => {
    const path = new URL(url, location.origin).pathname
    if (/\/api\/1\/auth\/user$/.test(path))
      return data && typeof data === 'object' ? { id: data.id, displayName: data.displayName, friends: Array.isArray(data.friends) ? data.friends : undefined } : null
    if (/\/api\/1\/auth\/user\/friends$/.test(path))
      return Array.isArray(data) ? data.map((u) => ({ id: u?.id, displayName: u?.displayName })) : null
    return data // ワールドは公開されている情報
  }
  const post = (url, body) => {
    try {
      const data = trim(url, JSON.parse(body))
      if (data != null) window.postMessage({ source: 'yamaori-hook', url, data }, location.origin)
    } catch { /* JSON でないものは見ない */ }
  }

  const origFetch = window.fetch
  window.fetch = async function (...args) {
    const res = await origFetch.apply(this, args)
    try {
      const url = typeof args[0] === 'string' ? args[0] : args[0]?.url ?? ''
      if (WATCH.test(url) && res.ok) res.clone().text().then((t) => post(url, t)).catch(() => {})
    } catch { /* ページの動きを妨げない */ }
    return res
  }

  const origOpen = XMLHttpRequest.prototype.open
  XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    this.__yamaoriUrl = String(url)
    return origOpen.call(this, method, url, ...rest)
  }
  const origSend = XMLHttpRequest.prototype.send
  XMLHttpRequest.prototype.send = function (...args) {
    this.addEventListener('load', () => {
      try {
        const url = this.__yamaoriUrl ?? ''
        if (WATCH.test(url) && this.status >= 200 && this.status < 300 && (this.responseType === '' || this.responseType === 'text')) post(url, this.responseText)
      } catch { /* 妨げない */ }
    })
    return origSend.apply(this, args)
  }
})()
