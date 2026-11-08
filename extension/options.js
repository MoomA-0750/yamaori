import { DEFAULTS, getSettings } from './common.js'

const $ = (id) => document.getElementById(id)
const s = await getSettings()
for (const k of Object.keys(DEFAULTS)) {
  const el = $(k)
  if (!el) continue
  if (el.type === 'checkbox') el.checked = !!s[k]
  else el.value = s[k]
}

async function save() {
  const next = {}
  for (const k of Object.keys(DEFAULTS)) {
    const el = $(k)
    if (!el) continue
    next[k] = el.type === 'checkbox' ? el.checked : el.type === 'number' ? Number(el.value) : el.value.trim()
  }
  next.bulkIntervalSec = Math.max(10, next.bulkIntervalSec || 10)
  next.bulkDailyLimit = Math.min(500, Math.max(1, next.bulkDailyLimit || 100))
  // localhost 以外の Yamaori（家の中・Tailscale）には、そのアドレスへの許可が要る
  const url = new URL(next.yamaoriUrl || DEFAULTS.yamaoriUrl)
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) {
    const ok = await chrome.permissions.request({ origins: [`${url.protocol}//${url.host}/*`] })
    if (!ok) { $('saved').textContent = 'そのアドレスへの許可がないので保存しませんでした'; return }
  }
  await chrome.storage.local.set(next)
  $('saved').textContent = '保存しました'
}
$('saveBtn').onclick = save

$('test').onclick = async () => {
  await save()
  const r = await chrome.runtime.sendMessage({ type: 'yamaori', method: 'GET', path: '/api/status' })
  $('testMsg').textContent = r.ok ? `つながりました（${r.result.deviceName}）` : `つながりません: ${r.error}`
  $('testMsg').className = r.ok ? 'ok' : 'err'
}
