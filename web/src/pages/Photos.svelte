<script lang="ts">
  import { api, type PhotoView } from '../api'
  import type { components } from '../schema'
  import { live } from '../live.svelte'
  import { href, router } from '../router.svelte'
  import PhotoGrid from '../components/PhotoGrid.svelte'
  import Loading from '../components/Loading.svelte'

  type Group = components['schemas']['PhotoGroup']

  // 「#/photos?world=…」「#/photos?person=…」「#/photos?by=world」を読む
  function params() { return new URLSearchParams(location.hash.split('?')[1] ?? '') }
  let query = $state(params())
  $effect(() => { router.route; query = params() })
  const world = $derived(query.get('world'))
  const person = $derived(query.get('person'))
  const by = $derived(world || person ? null : query.get('by'))

  let items = $state<PhotoView[]>([])
  let total = $state<number | null>(null)
  let error = $state('')
  let busy = $state(false)
  let groups = $state<Group[] | null>(null)
  let title = $state('')

  async function load(reset: boolean) {
    busy = true
    try {
      const page = await api.photos({ limit: 120, offset: reset ? 0 : items.length, world: world ?? undefined, person: person ?? undefined })
      items = reset ? page.items : [...items, ...page.items]
      total = page.total
    } catch (e) { error = (e as Error).message }
    busy = false
  }
  $effect(() => {
    live.version
    error = ''
    if (by === 'world' || by === 'person') {
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
    <a href={href.photos()} class:on={!by && !world && !person}>すべて</a>
    <a href={href.photos({ by: 'world' })} class:on={by === 'world' || !!world}>ワールドごと</a>
    <a href={href.photos({ by: 'person' })} class:on={by === 'person' || !!person}>人ごと</a>
  </nav>

  {#if by === 'world' || by === 'person'}
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
      <p class="faint">{total} 枚</p>
      {#each months as [month, list] (month)}
        <section>
          <h2 class="month">{month}</h2>
          <PhotoGrid photos={list} showDate />
        </section>
      {/each}
      {#if items.length < total}
        <button onclick={() => load(false)} disabled={busy}>もっと見る</button>
      {/if}
    {/if}
  {/if}
</div>

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
</style>
