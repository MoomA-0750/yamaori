<script lang="ts">
  import { api, type PersonDetail } from '../api'
  import { dateTime, day, localDate } from '../format'
  import { href } from '../router.svelte'
  import VisitList from '../components/VisitList.svelte'
  import Notes from '../components/Notes.svelte'
  import Loading from '../components/Loading.svelte'

  let { id }: { id: string } = $props()

  function friendLabel(e: import('../api').EventView): string {
    const d = e.data
    if (e.kind === 'notification') return d.direction === 'accepted' ? 'フレンド申請を承諾した' : d.direction === 'sent' ? 'フレンド申請を送った' : 'フレンド申請が届いた'
    switch (d.type) {
      case 'Friend': return 'フレンドになった'
      case 'Unfriend': return 'フレンドでなくなった'
      case 'FriendRequest': return 'フレンド申請を送った'
      case 'CancelFriendRequest': return 'フレンド申請を取り消した'
      case 'DisplayName': return `名前を変えた（${d.previous_name ?? ''} → ${e.name ?? ''}）`
      case 'TrustLevel': return `ランクが変わった（${d.previous_trust_level ?? ''} → ${d.trust_level ?? ''}）`
      default: return d.type ?? ''
    }
  }
  let data = $state<PersonDetail | null>(null)
  let error = $state('')

  let more = $state<import('../api').VisitSummary[]>([])
  let loadingMore = $state(false)
  async function loadMore(id: string, total: number) {
    if (!data) return
    loadingMore = true
    const shown = data.visits.length + more.length
    const page = await api.visits({ person: id, offset: shown, limit: 100 })
    more = [...more, ...page.items]
    loadingMore = false
    void total
  }

  $effect(() => {
    more = []
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
      {#if p.userId.startsWith('name:')}
        <p class="faint">ID の分からない古い記録の人です。同じ名前の別人や、名前を変えた同じ人が混ざることがあります。</p>
      {:else}
        <p class="row">
          <a class="chip" href={`https://vrchat.com/home/user/${p.userId}`} target="_blank" rel="noopener noreferrer">vrchat.com で開く ↗</a>
          <span class="faint mono break">{p.userId}</span>
        </p>
      {/if}
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

    {#if data.friendship.length}
      <section>
        <h2 class="section-title">フレンドの記録</h2>
        <ol class="names card">
          {#each data.friendship as e (e.id)}
            <li><span>{friendLabel(e)}</span> <span class="faint nowrap">{dateTime(e.at)}</span></li>
          {/each}
        </ol>
      </section>
    {/if}

    <Notes targetType="person" targetId={p.userId} notes={data.notes} placeholder="この人のことを書く（Markdown が使える）" />

    <section>
      <h2 class="section-title">一緒にいたとき <span class="faint">{p.visitCount}</span></h2>
      <VisitList visits={[...data.visits, ...more]} showDate />
      {#if data.visits.length + more.length < p.visitCount}
        <button class="more" onclick={() => loadMore(p.userId, p.visitCount)} disabled={loadingMore}>もっと見る</button>
      {/if}
    </section>
  </div>
{/if}

<style>
  .more { margin-top: .75rem; }
  .head { gap: .35rem; }
  .head p { margin: 0; }
  .names { list-style: none; margin: 0; padding: .25rem 0; }
  .names li { padding: .4rem 1rem; border-top: 1px solid var(--line); display: flex; justify-content: space-between; gap: .5rem 1rem; flex-wrap: wrap; }
  .names li:first-child { border-top: none; }
</style>
