<script lang="ts">
  import { api, type PhotoView } from '../api'
  import type { components } from '../schema'
  import { live } from '../live.svelte'
  import { href, router } from '../router.svelte'
  import PhotoGrid from '../components/PhotoGrid.svelte'
  import Loading from '../components/Loading.svelte'
  import AlbumPicker from '../components/AlbumPicker.svelte'

  type Group = components['schemas']['PhotoGroup']
  type Album = components['schemas']['Album']

  // 「#/photos?world=…」「#/photos?person=…」「#/photos?by=world」を読む
  function params() { return new URLSearchParams(location.hash.split('?')[1] ?? '') }
  let query = $state(params())
  $effect(() => { router.route; query = params() })
  const world = $derived(query.get('world'))
  const person = $derived(query.get('person'))
  const album = $derived(query.get('album'))
  const trash = $derived(query.get('trash') === '1')
  const by = $derived(world || person || album || trash ? null : query.get('by'))

  // 選んで、まとめてアルバムに入れる・ゴミ箱に入れる
  let selecting = $state(false)
  let selected = $state<string[]>([])
  let picker = $state<string[] | null>(null)
  let note = $state('')
  let albums = $state<Album[] | null>(null)
  let albumInfo = $state<Album | null>(null)
  let renaming = $state('')
  let trashTotal = $state(0)
  let emptying = $state(false)
  let confirmEmpty = $state(false)
  let newAlbum = $state('')

  let items = $state<PhotoView[]>([])
  let total = $state<number | null>(null)
  let error = $state('')
  let busy = $state(false)
  let groups = $state<Group[] | null>(null)
  let title = $state('')

  async function load(reset: boolean) {
    busy = true
    try {
      const page = await api.photos({ limit: 120, offset: reset ? 0 : items.length, world: world ?? undefined, person: person ?? undefined, album: album ?? undefined, trash: trash || undefined })
      items = reset ? page.items : [...items, ...page.items]
      total = page.total
    } catch (e) { error = (e as Error).message }
    busy = false
  }
  function reload() { selected = []; load(true); refreshCounts() }
  function refreshCounts() {
    api.trashCount().then((c) => (trashTotal = c.count)).catch(() => {})
    if (album) api.album(album).then((a) => { albumInfo = a; renaming = a.name }).catch(() => (albumInfo = null))
  }
  $effect(() => {
    live.version
    error = ''
    selecting = false
    selected = []
    note = ''
    confirmEmpty = false
    refreshCounts()
    if (by === 'album') {
      albums = null
      api.albums().then((a) => (albums = a)).catch((e) => (error = (e as Error).message))
    } else if (by === 'world' || by === 'person') {
      groups = null
      ;(by === 'world' ? api.photosByWorld() : api.photosByPerson()).then((g) => (groups = g)).catch((e) => (error = (e as Error).message))
    } else {
      total = null
      load(true)
    }
  })
  // 絞り込んでいるときの見出し（ワールドの名前・人の名前）
  $effect(() => {
    title = ''
    if (world) api.world(world).then((w) => (title = w.world.name ?? world)).catch(() => (title = world))
    else if (person) api.person(person).then((p) => (title = p.person.name ?? person)).catch(() => (title = person.replace(/^name:/, '')))
  })

  async function trashSelected() {
    const ids = selected
    const r = trash ? await api.restorePhotos(ids) : await api.trashPhotos(ids)
    note = trash ? `${r.count} 枚を戻しました` : `${r.count} 枚をゴミ箱に入れました（ゴミ箱から戻せます）`
    reload()
  }
  async function removeFromAlbum() {
    if (!album) return
    const r = await api.removeFromAlbum(album, selected)
    note = `${r.count} 枚をアルバムから外しました（写真は消していません）`
    reload()
  }
  async function renameAlbum() {
    if (!album || !renaming.trim()) return
    albumInfo = await api.updateAlbum(album, { name: renaming.trim() })
    note = '名前を変えました'
  }
  async function deleteAlbum() {
    if (!album) return
    await api.deleteAlbum(album)
    location.hash = href.photos({ by: 'album' })
  }
  async function empty() {
    emptying = true
    try {
      const r = await api.emptyTrash()
      note = `${r.photos} 枚を消しました（ファイル ${r.files} 個）${r.failed.length ? `。消せなかったもの: ${r.failed.join('、')}` : ''}`
      confirmEmpty = false
      reload()
    } catch (e) { note = (e as Error).message }
    emptying = false
  }

  const months = $derived.by(() => {
    const m = new Map<string, PhotoView[]>()
    for (const p of items) {
      const d = new Date(p.takenAt)
      const k = `${d.getFullYear()}年${d.getMonth() + 1}月`
      m.set(k, [...(m.get(k) ?? []), p])
    }
    return [...m.entries()]
  })
</script>

<div class="stack">
  <h1>写真</h1>
  <nav class="tabs" aria-label="写真の分け方">
    <a href={href.photos()} class:on={!by && !world && !person && !album && !trash}>すべて</a>
    <a href={href.photos({ by: 'world' })} class:on={by === 'world' || !!world}>ワールドごと</a>
    <a href={href.photos({ by: 'person' })} class:on={by === 'person' || !!person}>人ごと</a>
    <a href={href.photos({ by: 'album' })} class:on={by === 'album' || !!album}>アルバム</a>
    <a href={href.photos({ trash: '1' })} class:on={trash}>ゴミ箱{#if trashTotal}<span class="nums count">{trashTotal}</span>{/if}</a>
  </nav>

  {#if by === 'album'}
    <form class="row" onsubmit={async (e) => { e.preventDefault(); if (newAlbum.trim()) { const a = await api.createAlbum(newAlbum.trim()); newAlbum = ''; location.hash = href.photos({ album: a.id }) } }}>
      <input type="text" bind:value={newAlbum} placeholder="新しいアルバムの名前" aria-label="新しいアルバムの名前" maxlength="200" />
      <button type="submit" disabled={!newAlbum.trim()}>アルバムを作る</button>
    </form>
    {#if !albums}
      <Loading {error} />
    {:else if albums.length === 0}
      <p class="faint">まだアルバムがありません。写真を開いて「アルバムに入れる」か、「選ぶ」で何枚か選んで作れます。</p>
    {:else}
      <ul class="groups">
        {#each albums as a (a.id)}
          <li>
            <a class="group card" href={href.photos({ album: a.id })}>
              {#if a.coverPhotoId}<img src={api.thumb(a.coverPhotoId, 320)} alt="" loading="lazy" decoding="async" onerror={(e) => ((e.currentTarget as HTMLImageElement).style.visibility = 'hidden')} />{:else}<span class="nocover faint">空のアルバム</span>{/if}
              <span class="label">
                <span class="name break">{a.name}</span>
                <span class="faint nums">{a.count} 枚</span>
              </span>
            </a>
          </li>
        {/each}
      </ul>
    {/if}
  {:else if by === 'world' || by === 'person'}
    {#if !groups}
      <Loading {error} />
    {:else if groups.length === 0}
      <p class="faint">{by === 'world' ? 'ワールドの分かる写真がまだありません。' : '誰かと一緒のときの写真がまだありません。'}</p>
    {:else}
      <p class="faint">{by === 'person' ? '撮ったときに同じインスタンスにいた人ごと。' : '写真のメタデータか、撮った時刻にいたワールドごと。'}写真の多い順に {groups.length} {by === 'world' ? 'ワールド' : '人'}。</p>
      <ul class="groups">
        {#each groups as g (g.key)}
          <li>
            <a class="group card" href={href.photos(by === 'world' ? { world: g.key } : { person: g.key })}>
              <img src={api.thumb(g.latestPhotoId, 320)} alt="" loading="lazy" decoding="async" onerror={(e) => ((e.currentTarget as HTMLImageElement).style.visibility = 'hidden')} />
              <span class="label">
                <span class="name break">{g.name ?? g.key}</span>
                <span class="faint nums">{g.count} 枚</span>
              </span>
            </a>
          </li>
        {/each}
      </ul>
    {/if}
  {:else}
    {#if album && albumInfo}
      <div class="row filter">
        <a href={href.photos({ by: 'album' })}>← アルバム</a>
        <span class="faint">/</span>
        <form class="row rename" onsubmit={(e) => { e.preventDefault(); renameAlbum() }}>
          <input type="text" bind:value={renaming} aria-label="アルバムの名前" maxlength="200" />
          {#if renaming.trim() && renaming.trim() !== albumInfo.name}<button type="submit">名前を変える</button>{/if}
        </form>
        <button class="danger-text" onclick={deleteAlbum}>アルバムを消す（写真は残す）</button>
      </div>
    {/if}
    {#if trash}
      <div class="card pad stack">
        <p>ゴミ箱の写真は、ほかの一覧には出ません。ファイルはまだ消していないので、「戻す」で戻せます。</p>
        {#if total}
          {#if !confirmEmpty}
            <div><button onclick={() => (confirmEmpty = true)}>ゴミ箱を空にする…</button></div>
          {:else}
            <p class="warn">{total} 枚の写真のファイルを、この PC と NAS のフォルダから本当に消します。戻せません（写真の送り先に送った写しは消しません）。</p>
            <div class="row"><button class="danger" onclick={empty} disabled={emptying}>{emptying ? '消しています…' : `${total} 枚を消す`}</button><button onclick={() => (confirmEmpty = false)}>やめる</button></div>
          {/if}
        {/if}
      </div>
    {/if}
    {#if world || person}
      <div class="row filter">
        <a href={href.photos({ by: world ? 'world' : 'person' })}>← {world ? 'ワールドごと' : '人ごと'}</a>
        <span class="faint">/</span>
        <a href={world ? href.world(world) : href.person(person!)}><b class="break">{title}</b></a>
        {#if person}<span class="faint">がいたときの写真</span>{/if}
      </div>
    {/if}
    {#if total === null}
      <Loading {error} />
    {:else if total === 0}
      <div class="card empty">
        <p>{world || person ? '写真がありません。' : 'まだ写真がありません。'}</p>
        {#if !world && !person}<p class="faint">VRChat の写真のフォルダ（NAS のフォルダも足せる）にある写真が、撮った時刻とワールドつきで並びます。</p>{/if}
      </div>
    {:else}
      <div class="row tools">
        {#if !selecting}<span class="faint">{total} 枚</span>{/if}
        {#if !selecting}
          <button onclick={() => (selecting = true)}>選ぶ</button>
        {:else}
          <span class="nums">{total} 枚のうち {selected.length} 枚を選んでいます</span>
          <button onclick={() => (selected = items.map((p) => p.id))}>読んだ分を全部選ぶ</button>
          {#if !trash}<button onclick={() => (picker = selected)} disabled={!selected.length}>アルバムに入れる</button>{/if}
          {#if album}<button onclick={removeFromAlbum} disabled={!selected.length}>アルバムから外す</button>{/if}
          <button onclick={trashSelected} disabled={!selected.length}>{trash ? '戻す' : 'ゴミ箱へ'}</button>
          <button onclick={() => { selecting = false; selected = [] }}>選ぶのをやめる</button>
        {/if}
      </div>
      {#if note}<p class="muted" role="status">{note}</p>{/if}
      {#each months as [month, list] (month)}
        <section>
          <h2 class="month">{month}</h2>
          <PhotoGrid photos={list} showDate {selecting} bind:selected inTrash={trash} onchange={reload} />
        </section>
      {/each}
      {#if items.length < total}
        <button onclick={() => load(false)} disabled={busy}>もっと見る</button>
      {/if}
    {/if}
  {/if}
</div>

{#if picker}<AlbumPicker photoIds={picker} onclose={() => (picker = null)} ondone={(m) => { note = m; selecting = false; selected = [] }} />{/if}

<style>
  .month { color: var(--ink-2); font-size: .9rem; margin: .25rem 0 .5rem; }
  .empty { padding: 1.5rem; text-align: center; }
  .empty p { margin: .25rem 0; }
  .tabs { display: flex; gap: .25rem; flex-wrap: wrap; }
  .tabs a { padding: .35rem .85rem; border-radius: 999px; color: var(--ink-2); text-decoration: none; }
  .tabs a.on { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
  .filter { align-items: baseline; gap: .5rem; flex-wrap: wrap; }
  .groups { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(10.5rem, 1fr)); gap: .75rem; }
  .group { display: flex; flex-direction: column; overflow: hidden; padding: 0; text-decoration: none; color: inherit; height: 100%; }
  .group img { width: 100%; aspect-ratio: 16 / 9; object-fit: cover; background: var(--line); display: block; }
  .label { display: flex; flex-direction: column; gap: .1rem; padding: .5rem .65rem .6rem; }
  .name { font-weight: 600; font-size: .9rem; line-height: 1.35; }
  .nocover { display: grid; place-items: center; width: 100%; aspect-ratio: 16 / 9; background: var(--surface-2); font-size: .8rem; }
  .danger-text { color: var(--danger, #b3261e); }
  .count { margin-left: .35rem; font-size: .75rem; }
  .tools { gap: .5rem; flex-wrap: wrap; align-items: center; }
  .rename input { min-width: 0; width: 16rem; max-width: 60vw; font-weight: 600; }
  .warn { color: var(--danger, #b3261e); margin: 0; }
  .pad { padding: 1rem; }
  .pad p { margin: 0; }
</style>
