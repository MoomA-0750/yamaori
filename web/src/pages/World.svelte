<script lang="ts">
  import { api, type WorldDetail } from '../api'
  import { minutes, dateTime } from '../format'
  import { href } from '../router.svelte'
  import VisitList from '../components/VisitList.svelte'
  import Notes from '../components/Notes.svelte'
  import Loading from '../components/Loading.svelte'

  let { id }: { id: string } = $props()
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

  $effect(() => {
    more = []
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
        {w.visitCount} 回 · {minutes(w.minutes)}
        {#if w.firstVisitAt}· はじめて {dateTime(w.firstVisitAt)}{/if}
      </p>
      <p class="row">
        <a class="chip" href={`https://vrchat.com/home/world/${w.id}`} target="_blank" rel="noopener noreferrer">vrchat.com で開く ↗</a>
        <span class="faint mono break">{w.id}</span>
      </p>
    </header>
    <Notes targetType="world" targetId={w.id} notes={data.notes} placeholder="このワールドのことを書く（Markdown が使える）" />
    <section>
      <h2 class="section-title">行ったとき <span class="faint">{w.visitCount}</span></h2>
      <VisitList visits={[...data.visits, ...more]} showDate showWorld={false} />
      {#if data.visits.length + more.length < w.visitCount}
        <button class="more" onclick={() => loadMore(w.id, w.visitCount)} disabled={loadingMore}>もっと見る</button>
      {/if}
    </section>
  </div>
{/if}

<style>
  .more { margin-top: .75rem; }
  .head { gap: .35rem; }
  .head p { margin: 0; }
</style>
