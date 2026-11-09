<script lang="ts">
  import { api } from '../api'
  import type { components } from '../schema'
  import { dateTime } from '../format'
  type S = components['schemas']
  type Person = S['PhotoPerson']

  // 写真の情報（ワールド・撮った人・一緒にいた人）を直す。撮った時刻から当てたものを下書きにできる
  let { photoId, onclose, onsaved }: { photoId: string; onclose: () => void; onsaved?: () => void } = $props()
  let info = $state<S['PhotoInfo'] | null>(null)
  let msg = $state('')
  let busy = $state(false)
  let worldId = $state('')
  let worldName = $state('')
  let authorId = $state('')
  let authorName = $state('')
  let people = $state<Person[]>([])
  let newPerson = $state('')
  let confirmWrite = $state(false)
  let dialog = $state<HTMLDialogElement>()

  function fill(i: S['PhotoInfo']) {
    const e = i.edit, p = i.photo
    worldId = e?.worldId ?? p.worldId ?? ''
    worldName = e?.worldName ?? p.worldName ?? ''
    authorId = e?.authorId ?? p.authorId ?? ''
    authorName = e?.authorName ?? p.authorName ?? ''
    people = [...(e?.people ?? i.file?.players ?? [])]
  }
  $effect(() => { api.photoInfo(photoId).then((i) => { info = i; fill(i) }).catch((e) => (msg = (e as Error).message)) })
  $effect(() => { dialog?.showModal() })

  function useSuggestion() {
    const s = info?.suggestion
    if (!s) return
    if (s.worldId) { worldId = s.worldId; worldName = s.worldName ?? '' }
    if (s.authorId) { authorId = s.authorId; authorName = s.authorName ?? '' }
    const seen = new Set(people.map((p) => p.id ?? 'name:' + p.name))
    people = [...people, ...s.people.filter((p) => !seen.has(p.id ?? 'name:' + p.name))]
  }
  const key = (p: Person) => p.id ?? 'name:' + p.name
  async function save() {
    busy = true; msg = ''
    try {
      await api.savePhotoEdit(photoId, { worldId: worldId || null, worldName: worldName || null, authorId: authorId || null, authorName: authorName || null, people })
      info = await api.photoInfo(photoId)
      msg = '直しました（Yamaori の中だけ）'
      onsaved?.()
    } catch (e) { msg = (e as Error).message }
    busy = false
  }
  async function write() {
    busy = true; msg = ''
    try {
      await api.savePhotoEdit(photoId, { worldId: worldId || null, worldName: worldName || null, authorId: authorId || null, authorName: authorName || null, people })
      const r = await api.writePhoto(photoId)
      info = await api.photoInfo(photoId)
      msg = `写真のファイル ${r.files} 個に書きました${r.skipped.length ? `。書けなかったもの: ${r.skipped.join('、')}` : ''}`
      confirmWrite = false
      onsaved?.()
    } catch (e) { msg = (e as Error).message }
    busy = false
  }
  async function clear() {
    await api.clearPhotoEdit(photoId)
    info = await api.photoInfo(photoId)
    if (info) fill(info)
    msg = '直した情報を消しました（写真から読んだものに戻りました）'
    onsaved?.()
  }
</script>

<dialog bind:this={dialog} onclose={onclose} aria-label="写真の情報を直す">
  <div class="stack pad">
    <h2>写真の情報</h2>
    {#if !info}
      <p class="faint">{msg || '読んでいます…'}</p>
    {:else}
      <p class="faint">{dateTime(info.photo.takenAt)} · {info.photo.fileName} · {info.file?.worldId ? '写真のファイルにワールドの情報があります' : '写真のファイルにはワールドの情報がありません（下は直した・当てた情報）'}</p>
      {#if info.suggestion.worldId || info.suggestion.people.length}
        <div class="hint row">
          <span>撮った時刻にいたのは <b>{info.suggestion.worldName ?? info.suggestion.worldId ?? '（分からない）'}</b>{#if info.suggestion.people.length}、ほか {info.suggestion.people.length} 人{/if}</span>
          <button onclick={useSuggestion}>これを使う</button>
        </div>
      {:else}
        <p class="faint">撮った時刻の訪問の記録がないので、当てられません。</p>
      {/if}
      <div class="grid2">
        <label class="stack field">ワールドの名前<input type="text" bind:value={worldName} maxlength="300" /></label>
        <label class="stack field">ワールドの ID<input type="text" bind:value={worldId} placeholder="wrld_…" maxlength="300" /></label>
        <label class="stack field">撮った人<input type="text" bind:value={authorName} maxlength="300" /></label>
        <label class="stack field">撮った人の ID<input type="text" bind:value={authorId} placeholder="usr_…" maxlength="300" /></label>
      </div>
      <div class="stack">
        <span class="faint">一緒にいた人（{people.length} 人）</span>
        {#if people.length}
          <ul class="people">
            {#each people as p (key(p))}
              <li class="chip">{p.name}<button class="x" onclick={() => (people = people.filter((x) => key(x) !== key(p)))} aria-label={`${p.name} を外す`}>×</button></li>
            {/each}
          </ul>
        {/if}
        <form class="row" onsubmit={(e) => { e.preventDefault(); if (newPerson.trim()) { people = [...people, { id: null, name: newPerson.trim() }]; newPerson = '' } }}>
          <input type="text" bind:value={newPerson} placeholder="名前で足す" aria-label="一緒にいた人を名前で足す" maxlength="300" />
          <button type="submit" disabled={!newPerson.trim()}>足す</button>
        </form>
      </div>
      <div class="row">
        <button class="primary" onclick={save} disabled={busy}>保存（Yamaori の中だけ）</button>
        {#if info.edit}<button onclick={clear} disabled={busy}>直した情報を消す</button>{/if}
      </div>
      {#if info.canWrite}
        <div class="stack write">
          {#if !confirmWrite}
            <div><button onclick={() => (confirmWrite = true)} disabled={busy}>写真のファイルにも書く…</button>{#if info.edit?.writtenAt}<span class="faint"> 前に書いた: {dateTime(info.edit.writtenAt)}</span>{/if}</div>
          {:else}
            <p class="warn">写真のファイル（この PC と NAS にあるもの全部）に、VRChat と同じ形の情報（XMP）を書き込みます。画像はそのままですが、ファイルは書き換わります。WebDAV・SFTP の送り先に送った写しは、次に送るときに書いたもので送り直します。</p>
            <div class="row"><button class="danger-text" onclick={write} disabled={busy}>{busy ? '書いています…' : '書く'}</button><button onclick={() => (confirmWrite = false)}>やめる</button></div>
          {/if}
        </div>
      {/if}
      {#if msg}<p class="muted" role="status">{msg}</p>{/if}
    {/if}
    <div class="row end"><button onclick={() => dialog?.close()}>閉じる</button></div>
  </div>
</dialog>

<style>
  dialog { border: 0; padding: 0; background: var(--surface); color: var(--ink); border-radius: var(--radius); width: min(94vw, 40rem); max-height: 92vh; box-shadow: 0 10px 40px rgb(0 0 0 / 40%); }
  dialog::backdrop { background: rgb(0 0 0 / 55%); }
  .pad { padding: 1rem 1.1rem; }
  h2 { margin: 0; font-size: 1.05rem; }
  p { margin: 0; }
  .hint { background: var(--accent-soft); border-radius: var(--radius-sm); padding: .5rem .75rem; justify-content: space-between; align-items: center; gap: .5rem; flex-wrap: wrap; }
  .grid2 { display: grid; grid-template-columns: 2fr 3fr; gap: .5rem .75rem; }
  .grid2 input { font-size: .9rem; }
  .people { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: .35rem; }
  .people li { display: inline-flex; align-items: center; gap: .25rem; }
  .x { border: 0; background: none; padding: 0 .15rem; min-height: 0; color: var(--ink-2); cursor: pointer; }
  form input { flex: 1; min-width: 0; }
  .write { border-top: 1px solid var(--line); padding-top: .75rem; }
  .warn { color: var(--danger, #b3261e); }
  .danger-text { color: var(--danger, #b3261e); }
  .end { justify-content: flex-end; }
  @media (max-width: 560px) { .grid2 { grid-template-columns: 1fr; } }
</style>
