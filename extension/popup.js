import { getSettings } from './common.js'

const $ = (id) => document.getElementById(id)
const say = (text, cls = 'muted') => { $('msg').textContent = text; $('msg').className = cls }

const s = await getSettings()
$('bulk').hidden = !s.bulkOpen
$('options').onclick = () => chrome.runtime.openOptionsPage()
$('bulk').onclick = () => chrome.tabs.create({ url: chrome.runtime.getURL('bulk.html') })

// つながるか
const st = await chrome.runtime.sendMessage({ type: 'yamaori', method: 'GET', path: '/api/status' })
$('conn').textContent = st.ok ? `Yamaori（${st.result.deviceName}）につながっています` : `Yamaori につながりません: ${st.error}`
$('conn').className = st.ok ? 'ok' : 'err'

// 今のタブが vrchat.com なら、そのページの様子
const [tab] = await chrome.tabs.query({ active: true, currentWindow: true })
if (tab?.url?.startsWith('https://vrchat.com/home') && st.ok) {
  const state = await chrome.tabs.sendMessage(tab.id, { type: 'pageState' }).catch(() => null)
  if (state) {
    $('save').disabled = state.worlds === 0
    $('save').textContent = state.worlds > 1 ? `このページのワールドを保存（${state.worlds}）` : 'このページのワールドを保存'
    $('friends').disabled = state.friends === null
    if (state.worlds === 0) say('このページにはワールドの情報が見えていません。ワールドのページか、お気に入り・検索の一覧で使います。')
  }
} else if (st.ok) {
  say('vrchat.com のワールドのページで使います。')
}

$('save').onclick = async () => {
  const r = await chrome.tabs.sendMessage(tab.id, { type: 'savePage' }).catch((e) => ({ ok: false, error: String(e) }))
  say(r?.ok ? `保存しました（${r.count}）` : `保存できませんでした: ${r?.error ?? ''}`, r?.ok ? 'ok' : 'err')
}
$('friends').onclick = async () => {
  const r = await chrome.tabs.sendMessage(tab.id, { type: 'saveFriendsNow' }).catch((e) => ({ ok: false, error: String(e) }))
  if (!r?.ok) { say(`記録できませんでした: ${r?.error ?? ''}`, 'err'); return }
  const x = r.result
  say(x.first ? `フレンドの一覧（${x.count} 人）を初めて記録しました。次からは増えた・減った人が分かります。`
    : `フレンド ${x.count} 人。増えた ${x.added.length}・減った ${x.removed.length}・名前を変えた ${x.renamed.length}`, 'ok')
}
