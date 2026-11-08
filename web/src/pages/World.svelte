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

  $effect(() => {
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
      <h2 class="section-title">行ったとき <span class="faint">{data.visits.length}</span></h2>
      <VisitList visits={data.visits} showDate showWorld={false} />
    </section>
  </div>
{/if}

<style>
  .head { gap: .35rem; }
  .head p { margin: 0; }
</style>
