import { getSettings, toWorldDetails } from './common.js'

const $ = (id) => document.getElementById(id)
const s = await getSettings()
let stopping = false

const today = () => new Date().toISOString().slice(0, 10)
async function usedToday() {
  const { bulkUsed } = await chrome.storage.local.get({ bulkUsed: { date: '', count: 0 } })
  return bulkUsed.date === today() ? bulkUsed.count : 0
}
async function countOne() {
  await chrome.storage.local.set({ bulkUsed: { date: today(), count: (await usedToday()) + 1 } })
}
const log = (text, cls = '') => { const li = document.createElement('li'); li.textContent = text; li.className = cls; $('log').prepend(li) }
const call = (method, path, body) => chrome.runtime.sendMessage({ type: 'yamaori', method, path, body })

if (!s.bulkOpen) {
  $('status').textContent = '設定で「情報のないワールドをまとめて取る」を有効にすると使えます。'
} else {
  const left = s.bulkDailyLimit - (await usedToday())
  const r = await call('GET', `/api/worlds/missing-details?limit=${Math.max(1, Math.min(20, left))}`)
  if (!r.ok) $('status').textContent = `Yamaori につながりません: ${r.error}`
  else if (left <= 0) $('status').textContent = `今日の上限（${s.bulkDailyLimit} 件）に達しました。`
  else if (r.result.length === 0) $('status').textContent = '情報のないワールドはありません。'
  else {
    const list = r.result
    $('status').textContent = `${list.length} 件（最大 20 件ずつ。今日はあと ${left} 件）。1 件ごとに ${s.bulkIntervalSec} 秒あけます。`
    $('start').disabled = false
    $('start').onclick = () => run(list)
  }
}

async function run(list) {
  $('start').disabled = true
  $('stop').disabled = false
  $('stop').onclick = () => { stopping = true; $('status').textContent = '止めています…' }
  let done = 0
  for (const w of list) {
    if (stopping) break
    if ((await usedToday()) >= s.bulkDailyLimit) { log('今日の上限に達したので止めました'); break }
    $('status').textContent = `${done + 1} / ${list.length}: ${w.name ?? w.id}`
    const r = await chrome.runtime.sendMessage({ type: 'openAndCapture', worldId: w.id })
    await countOne()
    if (r?.ok && r.world) {
      const { id, body } = toWorldDetails(r.world)
      const saved = await call('PUT', `/api/worlds/${encodeURIComponent(id)}/details`, body)
      log(saved.ok ? `保存した: ${r.world.name}` : `保存できなかった: ${w.name ?? w.id}（${saved.error}）`, saved.ok ? 'ok' : 'err')
    } else {
      log(`取れなかった（消えたワールドか、読み込みが遅かった）: ${w.name ?? w.id}`, 'err')
    }
    done++
    if (done < list.length && !stopping) await new Promise((res) => setTimeout(res, s.bulkIntervalSec * 1000))
  }
  $('status').textContent = stopping ? `止めました（${done} 件）` : `終わりました（${done} 件）`
  $('stop').disabled = true
}
