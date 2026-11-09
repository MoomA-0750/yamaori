<script lang="ts">
  import { api, type WorldDetail } from '../api'
  import { minutes, dateTime, listLabel } from '../format'
  import { href } from '../router.svelte'
  import VisitList from '../components/VisitList.svelte'
  import Notes from '../components/Notes.svelte'
  import PhotoGrid from '../components/PhotoGrid.svelte'
  import type { PhotoView } from '../api'
  import Loading from '../components/Loading.svelte'

  let { id }: { id: string } = $props()
  let thumbFailed = $state(false)
  const platformLabel = (p: string) => ({ standalonewindows: 'PC', android: 'Quest・Android', ios: 'iOS' } as Record<string, string>)[p] ?? p
  let data = $state<WorldDetail | null>(null)
  let error = $state('')

  let more = $state<import('../api').VisitSummary[]>([])
  let loadingMore = $state(false)
  async function loadMore(id: string, total: number) {
    if (!data) return
    loadingMore = true
    const shown = data.visits.length + more.length
    const page = await api.visits({ world: id, offset: shown, limit: 100 })
    more = [...more, ...page.items]
    loadingMore = false
    void total
  }

  let photos = $state<PhotoView[]>([])
  let photoTotal = $state(0)

  $effect(() => {
    more = []
    api.photos({ world: id, limit: 60 }).then((p) => { photos = p.items; photoTotal = p.total }).catch(() => {})
    api.world(id).then((d) => (data = d)).catch((e) => (error = (e as Error).message))
  })
</script>

{#if !data}
  <Loading {error} />
{:else}
  {@const w = data.world}
  <div class="stack">
    <nav class="faint"><a href={href.worlds()}>← ワールド</a></nav>
    <header class="stack head">
      <h1 class="break">{w.name ?? w.id}</h1>
      <p class="muted">
        {#if w.visitCount}
          {w.visitCount} 回 · {minutes(w.minutes)}
          {#if w.firstVisitAt}· はじめて {dateTime(w.firstVisitAt)}{/if}
        {:else}
          訪問の記録はまだありません
        {/if}
      </p>
      <p class="row">
        <a class="chip" href={`https://vrchat.com/home/world/${w.id}`} target="_blank" rel="noopener noreferrer">vrchat.com で開く ↗</a>
        <span class="faint mono break">{w.id}</span>
      </p>
    </header>
    {#if data.info.description || data.info.authorName || data.info.thumbnailUrl || data.info.favorites.length || data.info.categories.length}
      <section class="info card" class:with-thumb={!!data.info.thumbnailUrl && !thumbFailed}>
        {#if data.info.thumbnailUrl && !thumbFailed}<img class="thumb" src={data.info.thumbnailUrl} alt="" loading="lazy" referrerpolicy="no-referrer" onerror={() => (thumbFailed = true)} />{/if}
        <div class="stack text">
          {#if data.info.authorName}<div class="muted">作者 {data.info.authorName}</div>{/if}
          {#if data.info.favorites.length}<div class="row">{#each data.info.favorites as f (f)}<span class="chip accent">★ {listLabel(f)}</span>{/each}</div>{/if}
          {#if data.info.categories.length}<div class="row">{#each data.info.categories as c (c)}<span class="chip">{c}</span>{/each}</div>{/if}
          {#if data.info.platforms.length || data.info.capacity || data.info.visitsTotal}
            <div class="faint">
              {#if data.info.platforms.length}{data.info.platforms.map(platformLabel).join('・')}{/if}
              {#if data.info.capacity} · <span class="nowrap">{data.info.capacity} 人まで</span>{/if}
              {#if data.info.visitsTotal} · <span class="nowrap">vrchat.com 全体の訪問 {data.info.visitsTotal.toLocaleString()}</span>{/if}
            </div>
          {/if}
          {#if data.info.description}<p class="desc break">{data.info.description}</p>{/if}
          {#if data.info.detailsSource}<div class="faint">情報の出どころ: {data.info.detailsSource === 'vrcx' ? 'VRCX' : data.info.detailsSource === 'extension' ? '拡張機能' : data.info.detailsSource}{data.info.detailsUpdatedAt ? `（${dateTime(data.info.detailsUpdatedAt)}）` : ''}</div>{/if}
        </div>
      </section>
    {/if}
    <Notes targetType="world" targetId={w.id} notes={data.notes} placeholder="このワールドのことを書く（Markdown が使える）" />
    {#if photos.length}
      <section>
        <h2 class="section-title">撮った写真 <span class="faint">{photoTotal}</span></h2>
        <PhotoGrid {photos} showDate />
      </section>
    {/if}
    {#if w.visitCount}<section>
      <h2 class="section-title">行ったとき <span class="faint">{w.visitCount}</span></h2>
      <VisitList visits={[...data.visits, ...more]} showDate showWorld={false} />
      {#if data.visits.length + more.length < w.visitCount}
        <button class="more" onclick={() => loadMore(w.id, w.visitCount)} disabled={loadingMore}>もっと見る</button>
      {/if}
    </section>{/if}
  </div>
{/if}

<style>
  .info { display: grid; grid-template-columns: 1fr; gap: 1rem; padding: .9rem 1rem; align-items: start; }
  .info.with-thumb { grid-template-columns: 12rem 1fr; }
  .info .thumb { width: 100%; aspect-ratio: 4 / 3; object-fit: cover; border-radius: var(--radius-sm); background: var(--surface-2); }
  .info .text { gap: .4rem; }
  .desc { margin: 0; white-space: pre-line; }
  @media (max-width: 480px) { .info.with-thumb { grid-template-columns: 1fr; } }
  .more { margin-top: .75rem; }
  .head { gap: .35rem; }
  .head p { margin: 0; }
</style>
