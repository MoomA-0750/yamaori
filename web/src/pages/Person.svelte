<script lang="ts">
  import { api, type PersonDetail } from '../api'
  import { dateTime, day, localDate } from '../format'
  import { href } from '../router.svelte'
  import VisitList from '../components/VisitList.svelte'
  import Notes from '../components/Notes.svelte'
  import Loading from '../components/Loading.svelte'

  let { id }: { id: string } = $props()
  let data = $state<PersonDetail | null>(null)
  let error = $state('')

  $effect(() => {
    api.person(id).then((d) => (data = d)).catch((e) => (error = (e as Error).message))
  })
</script>

{#if !data}
  <Loading {error} />
{:else}
  {@const p = data.person}
  <div class="stack">
    <nav class="faint"><a href={href.people()}>← 会った人</a></nav>
    <header class="stack head">
      <h1 class="break">{p.name ?? p.userId}</h1>
      <p class="muted">
        {p.visitCount} 回
        {#if p.firstMetAt}· はじめて {dateTime(p.firstMetAt)}{/if}
      </p>
      <p class="row">
        <a class="chip" href={`https://vrchat.com/home/user/${p.userId}`} target="_blank" rel="noopener noreferrer">vrchat.com で開く ↗</a>
        <span class="faint mono break">{p.userId}</span>
      </p>
    </header>

    {#if data.names.length > 1}
      <section>
        <h2 class="section-title">名前の移り変わり</h2>
        <ol class="names card">
          {#each data.names as n (n.name + n.firstSeenAt)}
            <li><span class="break">{n.name}</span> <span class="faint nowrap">{n.firstSeenAt ? day(localDate(n.firstSeenAt)) : ''} – {n.lastSeenAt ? day(localDate(n.lastSeenAt)) : ''}</span></li>
          {/each}
        </ol>
      </section>
    {/if}

    <Notes targetType="person" targetId={p.userId} notes={data.notes} placeholder="この人のことを書く（Markdown が使える）" />

    <section>
      <h2 class="section-title">一緒にいたとき <span class="faint">{data.visits.length}</span></h2>
      <VisitList visits={data.visits} showDate />
    </section>
  </div>
{/if}

<style>
  .head { gap: .35rem; }
  .head p { margin: 0; }
  .names { list-style: none; margin: 0; padding: .25rem 0; }
  .names li { padding: .4rem 1rem; border-top: 1px solid var(--line); display: flex; justify-content: space-between; gap: .5rem 1rem; flex-wrap: wrap; }
  .names li:first-child { border-top: none; }
</style>
