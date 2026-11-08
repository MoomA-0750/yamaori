<script lang="ts">
  import { api, type Page, type PersonSummary } from '../api'
  import { localDate, day } from '../format'
  import { href } from '../router.svelte'
  import Loading from '../components/Loading.svelte'

  let query = $state('')
  let sort = $state('last')
  let kind = $state('all')
  let page = $state<Page<PersonSummary> | null>(null)
  let error = $state('')
  let limit = $state(100)

  $effect(() => {
    const q = query, s = sort, l = limit, k = kind
    const t = setTimeout(() => api.people({ q: q || undefined, sort: s, limit: l, kind: k }).then((p) => (page = p)).catch((e) => (error = (e as Error).message)), q ? 200 : 0)
    return () => clearTimeout(t)
  })
</script>

<div class="stack">
  <h1>会った人</h1>
  <div class="tools">
    <input type="search" placeholder="名前で探す（前の名前でも）" bind:value={query} aria-label="人を名前で探す" />
    <select bind:value={sort} aria-label="並べ方">
      <option value="last">最後に会った順</option>
      <option value="visits">会った回数</option>
      <option value="name">名前</option>
    </select>
    <select bind:value={kind} aria-label="どの人を出すか">
      <option value="all">全員</option>
      <option value="id">ID の分かる人</option>
      <option value="name">名前だけの人</option>
    </select>
  </div>
  <p class="faint note">「名前だけ」の人は、ID の分からない古い記録（VRChatActivityTools から取り込んだもの）の人です。同じ名前の別人や、名前を変えた同じ人が混ざることがあります。</p>
  {#if !page}
    <Loading {error} />
  {:else}
    <p class="faint">{page.total} 人</p>
    <ul class="list card">
      {#each page.items as p (p.userId)}
        <li>
          <a href={href.person(p.userId)} class="item">
            <span class="name break">{p.name ?? p.userId}{#if p.userId.startsWith('name:')} <span class="chip small">名前だけ</span>{/if}</span>
            <span class="row faint nums">
              <span>{p.visitCount} 回</span>
              {#if p.lastMetAt}<span>{day(localDate(p.lastMetAt))}</span>{/if}
              {#if p.noteCount}<span class="chip accent">メモ</span>{/if}
            </span>
          </a>
        </li>
      {/each}
    </ul>
    {#if page.items.length < page.total}
      <button onclick={() => (limit += 100)}>もっと見る</button>
    {/if}
  {/if}
</div>

<style>
  .small { font-size: .7rem; padding: 0 .45rem; vertical-align: .1em; }
  .tools { display: grid; grid-template-columns: 1fr auto auto; gap: .5rem; }
  .note { margin: 0; }
  .tools select { width: auto; }
  .list { list-style: none; margin: 0; padding: .25rem 0; }
  .list li { border-top: 1px solid var(--line); }
  .list li:first-child { border-top: none; }
  .item { display: flex; justify-content: space-between; align-items: baseline; gap: .25rem 1rem; flex-wrap: wrap; padding: .55rem 1rem; }
  .nums { gap: .25rem .9rem; font-size: .85rem; font-variant-numeric: tabular-nums; }
  @media (max-width: 480px) { .tools { grid-template-columns: 1fr; } .tools select { width: 100%; } }
</style>
