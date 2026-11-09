<script lang="ts">
  import { api } from '../api'
  import type { components } from '../schema'
  type S = components['schemas']

  let { photoDirs }: { photoDirs: string[] } = $props()

  let view = $state<S['ThumbSettingsView'] | null>(null)
  let night = $state('')
  let host = $state('')
  let port = $state(22)
  let user = $state('')
  let jobs = $state(2)
  let dirs = $state<{ local: string; remote: string }[]>([])
  let msg = $state('')
  let remoteMsg = $state('')
  let remoteStatus = $state<S['RemoteThumbStatus'][] | null>(null)
  let timer: ReturnType<typeof setInterval> | undefined

  function apply(v: S['ThumbSettingsView']) {
    view = v
    night = v.night
    host = v.remote.host; port = v.remote.port; user = v.remote.user; jobs = v.remote.jobs
    dirs = v.remote.dirs.length ? v.remote.dirs.map((d) => ({ ...d })) : photoDirs.filter((d) => d.startsWith('\\\\') || d.startsWith('/')).map((d) => ({ local: d, remote: '' }))
  }
  api.thumbSettings().then(apply).catch(() => {})

  // 作っている間は、様子を読み直す
  $effect(() => {
    const running = view?.job.running || view?.job.nowRequested
    if (running && !timer) timer = setInterval(() => api.thumbSettings().then((v) => { if (view) view.job = v.job }).catch(() => {}), 2000)
    if (!running && timer) { clearInterval(timer); timer = undefined }
    return () => { if (timer) { clearInterval(timer); timer = undefined } }
  })

  async function save() {
    msg = ''
    try {
      apply(await api.saveThumbSettings({ night, host, port, user, jobs, dirs: dirs.filter((d) => d.local.trim() && d.remote.trim()) }))
      msg = '保存しました（夜の時間帯は起こし直すと効きます）'
    } catch (e) { msg = (e as Error).message }
  }
  async function now() { if (view) view.job = await api.thumbsNow() }
  async function stop() { if (view) view.job = await api.thumbsStop() }
  async function newKey() {
    if (view?.remote.keySet && !confirm('鍵を作り直すと、前の公開鍵では入れなくなります。作り直しますか？')) return
    const r = await api.remoteThumbKey()
    if (view) { view.remote.publicKey = r.text; view.remote.keySet = true }
  }
  async function run(f: () => Promise<S['ThumbMessage']>) {
    remoteMsg = 'つないでいます…'
    try { const r = await f(); remoteMsg = r.ok ? r.text : `できません: ${r.text}` } catch (e) { remoteMsg = (e as Error).message }
  }
  async function status() {
    remoteMsg = '読んでいます…'
    try { remoteStatus = await api.remoteThumbStatus(); remoteMsg = '' } catch (e) { remoteMsg = (e as Error).message }
  }
  async function copyKey() { if (view?.remote.publicKey) await navigator.clipboard.writeText(view.remote.publicKey) }
</script>

{#if view}
  <section class="card pad stack">
    <h2>写真の縮小</h2>
    <p class="faint">一覧に出す小さな縮小を前もって作って、この端末に置いておきます（NAS から写真を読むと 1 枚に数秒かかるため）。新しい写真から順に作ります。</p>
    <label class="stack field">夜に作る時間帯（空なら夜には作らない）<input type="text" bind:value={night} placeholder="01:00-07:00" /></label>
    <div class="row">
      {#if view.job.running || view.job.nowRequested}
        <button onclick={stop}>やめる</button>
      {:else}
        <button onclick={now}>今すぐ全部作る</button>
      {/if}
      <span class="faint nums">
        {#if view.job.running}{view.job.mode === 'now' ? '作っています' : '夜の分を作っています'} {view.job.done} / {view.job.total}{#if view.job.failed}（読めなかった {view.job.failed}）{/if}
        {:else if view.job.nowRequested}始めています…
        {:else if view.job.finishedAt}前に作った分: {view.job.made} 枚{/if}
      </span>
    </div>

    <h3>NAS の上で作る</h3>
    <p class="faint">写真のある NAS に SSH で入り、NAS の上で縮小を作ります（NAS の中で読むので速い）。NAS に Python 3 と Pillow が要ります（TrueNAS SCALE には入っています）。縮小は写真のフォルダの <code>.yamaori</code> にでき、この端末は写真を読まずにそれを使います。</p>
    <div class="grid3">
      <label class="stack field">NAS のアドレス<input type="text" bind:value={host} placeholder="192.168.1.10" autocomplete="off" /></label>
      <label class="stack field">番号<input type="number" min="1" max="65535" bind:value={port} /></label>
      <label class="stack field">ユーザー名<input type="text" bind:value={user} autocomplete="off" /></label>
    </div>
    <div class="stack">
      <span class="faint">写真のフォルダ（この端末から見た道 → NAS の上の道）</span>
      {#each dirs as d, i (i)}
        <div class="pair">
          <input type="text" bind:value={d.local} placeholder="\\NAS\photo\VRChat" aria-label="この端末から見た道" />
          <span class="arrow" aria-hidden="true">→</span>
          <input type="text" bind:value={d.remote} placeholder="/mnt/pool/photo/VRChat" aria-label="NAS の上の道" />
          <button onclick={() => dirs.splice(i, 1)} aria-label="この組を外す">外す</button>
        </div>
      {/each}
      <div><button onclick={() => dirs.push({ local: '', remote: '' })}>組を足す</button></div>
    </div>
    <label class="stack field narrow">NAS で同時に読む数<input type="number" min="1" max="8" bind:value={jobs} /></label>

    <div class="stack key">
      {#if view.remote.publicKey}
        <span class="faint">この公開鍵を、NAS のユーザーの SSH 公開鍵に登録します（TrueNAS なら「資格情報 → ユーザー → 編集 → SSH 公開鍵」）。</span>
        <code class="mono break pub">{view.remote.publicKey}</code>
        <div class="row"><button onclick={copyKey}>コピー</button><button onclick={newKey}>鍵を作り直す</button></div>
      {:else}
        <div class="row"><button onclick={newKey}>NAS に入る鍵を作る</button></div>
      {/if}
    </div>

    <div class="row"><button class="primary" onclick={save}>保存</button></div>
    <div class="row">
      <button onclick={() => run(api.remoteThumbTest)} disabled={!view.remote.keySet}>試しにつなぐ</button>
      <button onclick={() => run(api.remoteThumbStart)} disabled={!view.remote.keySet}>NAS で今すぐ作る</button>
      <button onclick={status} disabled={!view.remote.keySet}>NAS の様子</button>
      <button onclick={() => run(api.remoteThumbForgetHostKey)}>NAS の鍵を覚え直す</button>
    </div>
    {#if msg}<p class="muted">{msg}</p>{/if}
    {#if remoteMsg}<p class="muted">{remoteMsg}</p>{/if}
    {#if remoteStatus}
      <ul class="plain">
        {#each remoteStatus as s (s.remote)}
          <li class="faint nums">{s.remote}: {s.problem ?? `${s.running ? '作っています' : '止まっています'} ${s.made + s.skipped} / ${s.total}${s.failed ? `（読めなかった ${s.failed}）` : ''}`}</li>
        {/each}
      </ul>
    {/if}
    <p class="faint">夜の時間帯の始まりに、NAS でも作らせます。NAS の鍵は最初につないだときに覚え、変わったらつなぎません。</p>
  </section>
{/if}

<style>
  h3 { margin: .5rem 0 0; font-size: 1rem; }
  .grid3 { display: grid; grid-template-columns: 2fr 1fr 2fr; gap: .5rem; }
  .pair { display: grid; grid-template-columns: 1fr auto 1fr auto; gap: .5rem; align-items: center; }
  .narrow { max-width: 12rem; }
  .pub { font-size: .75rem; padding: .5rem; background: var(--bg-soft, rgba(0,0,0,.04)); border-radius: .5rem; }
  .plain { list-style: none; margin: 0; padding: 0; }
  @media (max-width: 640px) {
    .grid3 { grid-template-columns: 1fr; gap: 1rem; }
    .pair { grid-template-columns: 1fr; gap: .35rem; padding-bottom: .75rem; border-bottom: 1px solid var(--line); }
    .pair .arrow { transform: rotate(90deg); justify-self: start; padding-left: .5rem; }
    .pair button { justify-self: start; }
  }
</style>
