<script lang="ts">
  import { api, type Page, type WorldSummary } from '../api'
  import { localDate, day, minutes } from '../format'
  import { href } from '../router.svelte'
  import Loading from '../components/Loading.svelte'

  let query = $state('')
  let sort = $state('last')
  // ?list=* などで来たら、その一覧に絞る
  let list = $state(new URLSearchParams(location.hash.split('?')[1] ?? '').get('list') ?? '')
  let lists = $state<{ name: string; count: number }[]>([])
  api.worldLists().then((l) => (lists = l)).catch(() => {})
  let page = $state<Page<WorldSummary> | null>(null)
  let error = $state('')
  let limit = $state(50)

  $effect(() => {
    const q = query, s = sort, l = limit, li = list
    const t = setTimeout(() => api.worlds({ q: q || undefined, sort: s, limit: l, list: li || undefined }).then((p) => (page = p)).catch((e) => (error = (e as Error).message)), q ? 200 : 0)
    return () => clearTimeout(t)
  })
</script>

<div class="stack">
  <h1>ワールド</h1>
  <div class="tools">
    <input type="search" placeholder="名前で探す" bind:value={query} aria-label="ワールドを名前で探す" />
    <select bind:value={sort} aria-label="並べ方">
      <option value="last">最後に行った順</option>
      <option value="visits">行った回数</option>
      <option value="minutes">いた時間</option>
      <option value="name">名前</option>
      <option value="saved">保存した順</option>
    </select>
    <select bind:value={list} aria-label="どのワールドを出すか">
      <option value="">全部</option>
      <option value="*">保存・お気に入り</option>
      {#each lists as l (l.name)}<option value={l.name}>★ {l.name}（{l.count}）</option>{/each}
    </select>
  </div>
  {#if !page}
    <Loading {error} />
  {:else}
    <p class="faint">{page.total} か所</p>
    <ul class="list">
      {#each page.items as w (w.id)}
        <li>
          <a class="item card" href={href.world(w.id)}>
            <div class="name break">{w.name ?? w.id}</div>
            <div class="row faint nums">
              {#if w.visitCount}
                <span>{w.visitCount} 回</span>
                <span>{minutes(w.minutes)}</span>
              {:else}
                <span>まだ行っていない</span>
              {/if}
              {#if w.lastVisitAt}<span>最後 {day(localDate(w.lastVisitAt))}</span>{/if}
              {#if w.noteCount}<span class="chip accent">メモ {w.noteCount}</span>{/if}
            </div>
          </a>
        </li>
      {/each}
    </ul>
    {#if page.items.length < page.total}
      <button onclick={() => (limit += 50)}>もっと見る</button>
    {/if}
  {/if}
</div>

<style>
  .tools { display: grid; grid-template-columns: 1fr auto auto; gap: .5rem; }
  .tools select { width: auto; }
  .list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .5rem; }
  .item { display: block; padding: .75rem 1rem; }
  .item:hover { border-color: var(--accent); color: inherit; }
  .name { font-weight: 600; }
  .nums { gap: .25rem .9rem; font-size: .85rem; font-variant-numeric: tabular-nums; }
  @media (max-width: 480px) { .tools { grid-template-columns: 1fr; } .tools select { width: 100%; } }
</style>
