// 拡張機能の試し。vrchat.com への通信は全部ブラウザの中で横取りし、本物には行かない。
import { createRequire } from 'node:module'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PW)
const EXT = process.env.EXT, Y = process.env.Y, TOKEN = process.env.TOKEN, PROFILE = process.env.PROFILE

const world = (id, name, desc) => ({ id, name, authorId: 'usr_author', authorName: '作者さん', description: desc, imageUrl: 'https://img/x.png',
  thumbnailImageUrl: 'https://img/x_t.png', releaseStatus: 'public', capacity: 32, tags: ['author_tag_chill'], visits: 1234, favorites: 56,
  unityPackages: [{ platform: 'standalonewindows' }, { platform: 'android' }] })
let friends = ['usr_a', 'usr_b']
const page = (id) => `<!doctype html><html><body><h1>fake vrchat</h1><script>
  fetch('/api/1/auth/user').then(r => r.json());
  fetch('/api/1/auth/user/friends?offline=false&n=100').then(r => r.json());
  ${id ? `fetch('/api/1/worlds/${id}').then(r => r.json());` : `fetch('/api/1/worlds?featured=false&n=2').then(r => r.json());`}
</script></body></html>`

const https = require('node:https'), fs = require('node:fs')
const fake = https.createServer({ key: fs.readFileSync('key.pem'), cert: fs.readFileSync('cert.pem') }, (req, res) => {
  const u = new URL(req.url, 'https://vrchat.com')
  const route = { fulfill: ({ status = 200, contentType = 'text/html', body = '' }) => { res.writeHead(status, { 'content-type': contentType }); res.end(body) } }
  handle(u, route)
})
await new Promise((r) => fake.listen(18443, '127.0.0.1', r))
const ctx = await chromium.launchPersistentContext(PROFILE, {
  channel: 'chromium', headless: true, executablePath: process.env.CHROME, ignoreHTTPSErrors: true,
  args: [`--disable-extensions-except=${EXT}`, `--load-extension=${EXT}`,
    '--host-resolver-rules=MAP vrchat.com 127.0.0.1:18443, MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1', '--ignore-certificate-errors'],
})
function handle(u, route) {
  const json = (d) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(d) })
  if (u.pathname === '/api/1/auth/user') return json({ id: 'usr_me', displayName: 'Me', friends })
  if (u.pathname === '/api/1/auth/user/friends') return json(friends.map((id) => ({ id, displayName: id.toUpperCase() })))
  if (u.pathname.startsWith('/api/1/worlds/')) { const id = u.pathname.split('/').pop(); return json(world(id, id === 'wrld_test' ? 'テストのワールド' : 'まだ情報のないワールド', 'QV Pen と YamaPlayer があります')) }
  if (u.pathname === '/api/1/worlds') return json([world('wrld_list1', '一覧1', 'mirror'), world('wrld_list2', '一覧2', '')])
  if (u.pathname.startsWith('/home/world/')) return route.fulfill({ contentType: 'text/html', body: page(u.pathname.split('/').pop()) })
  if (u.pathname.startsWith('/home')) return route.fulfill({ contentType: 'text/html', body: page(null) })
  return route.fulfill({ status: 404, body: '' })
}

let [sw] = ctx.serviceWorkers()
if (!sw) sw = await ctx.waitForEvent('serviceworker')
const setSettings = (s) => sw.evaluate((s) => chrome.storage.local.set(s), s)
await setSettings({ yamaoriUrl: Y, token: TOKEN, autoWorlds: false, autoFriends: false, bulkOpen: false })
const api = async (path) => (await fetch(Y + path)).json()
const out = {}

// 1. 既定: ボタンを押したときだけ送る
const p = await ctx.newPage()
await p.goto('https://vrchat.com/home/world/wrld_test')
const btn = p.locator('button', { hasText: 'Yamaori に保存' })
await btn.waitFor({ timeout: 10000 })
out.beforeClick = (await api('/api/worlds/wrld_test').catch(() => null))?.info?.detailsSource ?? 'なし'
await btn.click()
await p.waitForTimeout(1500)
const w = await api('/api/worlds/wrld_test')
out.afterClick = { source: w.info.detailsSource, platforms: w.info.platforms, categories: w.info.categories, capacity: w.info.capacity, name: w.world.name }
await p.screenshot({ path: process.env.SHOT })

// 2. 自動でフレンドの一覧（初めて → 変えてもう一度）
await setSettings({ autoFriends: true })
await p.goto('https://vrchat.com/home')
await p.waitForTimeout(1500)
friends = ['usr_a', 'usr_c']
const p2 = await ctx.newPage()   // 10 分に 1 回の制限はページごとなので、新しいページで
await p2.goto('https://vrchat.com/home')
await p2.waitForTimeout(1500)
out.friendEvents = (await api('/api/events?kind=friend')).items.map((e) => `${e.data.type}:${e.userId}`).sort()

// 3. まとめて取る
await setSettings({ bulkOpen: true, bulkIntervalSec: 10 })
const extId = sw.url().split('/')[2]
const b = await ctx.newPage()
await b.goto(`chrome-extension://${extId}/bulk.html`)
await b.locator('#start:not([disabled])').waitFor({ timeout: 10000 })
out.bulkStatus = await b.locator('#status').textContent()
await b.click('#start')
await b.locator('#status', { hasText: '終わりました' }).waitFor({ timeout: 60000 })
out.bulkLog = await b.locator('#log').innerText()
out.missingAfter = (await api('/api/worlds/missing-details')).length
out.tabsLeft = ctx.pages().length

console.log(JSON.stringify(out, null, 1))
await ctx.close()
fake.close()
