<script lang="ts">
  import { api } from '../api'
  import type { components } from '../schema'
  import { dateTime } from '../format'
  type S = components['schemas']

  // VRChatActivityTools からの移行と、VRCX との連携
  let { member = false }: { member?: boolean } = $props()
  let view = $state<S['ImportView'] | null>(null)
  let msg = $state('')
  let replace = $state(false)
  let account = $state('')
  let vatFile = $state<FileList | undefined>()
  let vrcxFile = $state<FileList | undefined>()
  let vrcxPath = $state('')
  let vrcxAuto = $state(false)
  let vrcxMinutes = $state(60)
  let sending = $state(false)
  let timer: ReturnType<typeof setInterval> | undefined

  function apply(v: S['ImportView']) {
    view = v
    vrcxPath = v.vrcxLocal.pathSet ? v.vrcxLocal.path : ''
    vrcxAuto = v.vrcxLocal.auto
    vrcxMinutes = v.vrcxLocal.minutes
  }
  async function load() { try { apply(await api.importState()) } catch (e) { msg = (e as Error).message } }
  load()
  // 取り込んでいる間は様子を読み直す
  $effect(() => {
    if (view?.job.running && !timer) timer = setInterval(async () => { try { const v = await api.importState(); if (view) { view.job = v.job; view.vat = v.vat; view.vrcx = v.vrcx } } catch { /* 次の回 */ } }, 2000)
    if (!view?.job.running && timer) { clearInterval(timer); timer = undefined }
    return () => { if (timer) { clearInterval(timer); timer = undefined } }
  })

  async function send(kind: 'vat' | 'vrcx', files: FileList | undefined) {
    const f = files?.[0]
    if (!f) return
    sending = true; msg = `${f.name} を送っています…`
    try {
      const r = await api.importFile(kind, f, replace, kind === 'vat' ? account.trim() || undefined : undefined)
      msg = r.message
      await load()
    } catch (e) { msg = (e as Error).message }
    sending = false
  }
  async function local() {
    msg = ''
    try { const r = await api.importVrcxLocal(replace); msg = r.message; await load() } catch (e) { msg = (e as Error).message }
  }
  async function save() {
    try { apply(await api.saveImportSettings({ vrcxPath, vrcxAuto, vrcxMinutes })); msg = '保存しました' } catch (e) { msg = (e as Error).message }
  }
</script>

{#if view}
  <section class="card pad stack">
    <h2>取り込み</h2>
    {#if member}<p class="faint">この端末はグループのメンバーなので、取り込む DB はまとめ役へ送って、まとめ役で取り込みます。</p>{/if}
    {#if view.job.running}
      <p class="status" role="status">{view.job.kind === 'vat' ? 'VRChatActivityTools' : 'VRCX'} を取り込んでいます…（何年分もあると数分かかります）</p>
    {:else if view.job.finishedAt}
      <p class="status" class:bad={!view.job.ok} role="status">{view.job.ok ? '取り込みました' : '取り込めませんでした'}: {view.job.message}</p>
    {/if}

    <div class="stack block">
      <h3>VRChatActivityTools から移る</h3>
      <p class="faint">VRChatActivityTools の記録（<code>VRChatActivityLog.db</code>）を選ぶと、訪問・会った人・動画・通知を取り込みます。ログの記録がある期間とは重ねません。</p>
      <input type="file" bind:files={vatFile} accept=".db,.sqlite,.sqlite3" aria-label="VRChatActivityLog.db" />
      <label class="stack field narrow">自分のアカウントの ID（分かれば。usr_…）<input type="text" bind:value={account} placeholder="usr_…" /></label>
      <div><button class="primary" onclick={() => send('vat', vatFile)} disabled={!vatFile?.length || sending || view.job.running}>取り込む</button></div>
      {#if view.vat.at}<p class="faint">前に取り込んだ: {dateTime(view.vat.at)} — {view.vat.message}</p>{/if}
    </div>

    <div class="stack block">
      <h3>VRCX と連携する</h3>
      <p class="faint">VRCX の記録（<code>VRCX.sqlite3</code>）から、訪問・会った人・フレンドの増減・ワールドの情報・お気に入り・メモを取り込みます。VRCX の DB は読むだけです。</p>
      <p class="faint">この端末の VRCX: {view.vrcxLocal.exists ? `${view.vrcxLocal.path}（${view.vrcxLocal.modified ? dateTime(view.vrcxLocal.modified) : ''} に更新）` : `見つかりません（${view.vrcxLocal.path}）`}</p>
      <div class="row">
        <button onclick={local} disabled={!view.vrcxLocal.exists || view.job.running}>この端末の VRCX を今取り込む</button>
      </div>
      <label class="check"><input type="checkbox" bind:checked={vrcxAuto} /> 決まった間隔で取り込み続ける（VRCX が書き足した分が入ります）</label>
      <div class="grid2">
        <label class="stack field">間隔（分）<input type="number" min="5" max="1440" bind:value={vrcxMinutes} /></label>
        <label class="stack field">VRCX の DB の場所（空なら標準の場所）<input type="text" bind:value={vrcxPath} placeholder={view.vrcxLocal.path} /></label>
      </div>
      <div><button onclick={save}>保存</button></div>
      <p class="faint">グループに入っているときは、まとめ役の画面で「VRCX と連携する」の役を付けた端末が取り込み続けます。</p>
      <details>
        <summary class="faint">ほかの PC の VRCX の DB を選んで取り込む</summary>
        <div class="stack">
          <input type="file" bind:files={vrcxFile} accept=".sqlite3,.sqlite,.db" aria-label="VRCX.sqlite3" />
          <div><button onclick={() => send('vrcx', vrcxFile)} disabled={!vrcxFile?.length || sending || view.job.running}>取り込む</button></div>
        </div>
      </details>
      {#if view.vrcx.at}<p class="faint">前に取り込んだ: {dateTime(view.vrcx.at)} — {view.vrcx.message}</p>{/if}
    </div>

    <label class="check"><input type="checkbox" bind:checked={replace} /> 前に取り込んだものと別の DB なら、入れ替える（前の取り込みの分は消えます）</label>
    {#if view.job.needsReplace && !view.job.running}<p class="warn">前に取り込んだものと別の DB です。入れ替えてよければ、上の印を付けてもう一度取り込みます。</p>{/if}
    {#if msg}<p class="muted" role="status">{msg}</p>{/if}
  </section>
{/if}

<style>
  h3 { margin: 0; font-size: 1rem; }
  p { margin: 0; }
  .block { border-top: 1px solid var(--line); padding-top: .75rem; }
  .status { background: var(--accent-soft); border-radius: var(--radius-sm); padding: .5rem .75rem; }
  .status.bad, .warn { color: var(--danger, #b3261e); }
  .narrow { max-width: 24rem; }
  .grid2 { display: grid; grid-template-columns: 8rem 1fr; gap: .5rem .75rem; }
  @media (max-width: 560px) { .grid2 { grid-template-columns: 1fr; } }
</style>
