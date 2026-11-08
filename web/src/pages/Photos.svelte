<script lang="ts">
  import { api, type PhotoView } from '../api'
  import { live } from '../live.svelte'
  import PhotoGrid from '../components/PhotoGrid.svelte'
  import Loading from '../components/Loading.svelte'

  let items = $state<PhotoView[]>([])
  let total = $state<number | null>(null)
  let error = $state('')
  let busy = $state(false)

  async function load(reset: boolean) {
    busy = true
    try {
      const page = await api.photos({ limit: 120, offset: reset ? 0 : items.length })
      items = reset ? page.items : [...items, ...page.items]
      total = page.total
    } catch (e) { error = (e as Error).message }
    busy = false
  }
  $effect(() => { live.version; load(true) })

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
  {#if total === null}
    <Loading {error} />
  {:else if total === 0}
    <div class="card empty">
      <p>まだ写真がありません。</p>
      <p class="faint">VRChat の写真のフォルダ（NAS のフォルダも足せる）にある写真が、撮った時刻とワールドつきで並びます。</p>
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
</div>

<style>
  .month { color: var(--ink-2); font-size: .9rem; margin: .25rem 0 .5rem; }
  .empty { padding: 1.5rem; text-align: center; }
  .empty p { margin: .25rem 0; }
</style>
