<script lang="ts">
  import { api, type PersonDetail } from '../api'
  import { dateTime, day, localDate, friendLabel as friendStatus } from '../format'
  import { href } from '../router.svelte'
  import VisitList from '../components/VisitList.svelte'
  import Notes from '../components/Notes.svelte'
  import Loading from '../components/Loading.svelte'
  import PhotoGrid from '../components/PhotoGrid.svelte'

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

  // 忘れる（押すと確かめの欄を出し、もう一度押して忘れる）
  let forgetOpen = $state(false)
  let forgetMsg = $state('')
  async function forget() {
    if (!data) return
    try {
      await api.forgetPerson(data.person.userId, data.person.name ?? null)
      location.hash = href.people()
    } catch (e) {
      forgetMsg = (e as Error).message
    }
  }

  let photos = $state<import('../api').PhotoView[]>([])
  let photoTotal = $state(0)

  $effect(() => {
    more = []
    photos = []
    api.person(id).then((d) => (data = d)).catch((e) => (error = (e as Error).message))
    api.photos({ person: id, limit: 24 }).then((p) => { photos = p.items; photoTotal = p.total }).catch(() => {})
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
          {#if friendStatus(p.friend)}<span class="chip" class:accent={p.friend === 'friend'}>{friendStatus(p.friend)}</span>{/if}
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

    {#if photos.length}
      <section>
        <h2 class="section-title">一緒にいたときの写真 <span class="faint">{photoTotal}</span></h2>
        <PhotoGrid {photos} showDate />
        {#if photoTotal > photos.length}<a class="more-link" href={href.photos({ person: p.userId })}>全部見る（{photoTotal} 枚）→</a>{/if}
      </section>
    {/if}

    <section>
      <h2 class="section-title">一緒にいたとき <span class="faint">{p.visitCount}</span></h2>
      <VisitList visits={[...data.visits, ...more]} showDate />
      {#if data.visits.length + more.length < p.visitCount}
        <button class="more" onclick={() => loadMore(p.userId, p.visitCount)} disabled={loadingMore}>もっと見る</button>
      {/if}
    </section>

    <section class="forget">
      {#if !forgetOpen}
        <button class="ghost danger" onclick={() => (forgetOpen = true)}>この人を忘れる</button>
      {:else}
        <div class="card pad stack">
          <h2>この人を忘れますか</h2>
          <p class="muted">会った記録・この人の出来事（動画・通知・フレンドの記録）・この人へのメモを消し、あとからログを読み直したり取り込み直したりしても、この人は入れません。</p>
          <p class="faint">VRChat の生のログ（Yamaori のデータのフォルダの logs）には残ります。忘れるのは設定の「忘れた人」でやめられますが、消したメモは戻りません。</p>
          <div class="row">
            <button class="danger" onclick={forget}>忘れる</button>
            <button onclick={() => (forgetOpen = false)}>やめる</button>
          </div>
          {#if forgetMsg}<p class="error">{forgetMsg}</p>{/if}
        </div>
      {/if}
    </section>
  </div>
{/if}

<style>
  .more { margin-top: .75rem; }
  .forget { margin-top: 1rem; }
  .forget p { margin: 0; }
  .pad { padding: 1rem 1.2rem; }
  .error { color: var(--danger); }
  .head { gap: .35rem; }
  .head p { margin: 0; }
  .names { list-style: none; margin: 0; padding: .25rem 0; }
  .names li { padding: .4rem 1rem; border-top: 1px solid var(--line); display: flex; justify-content: space-between; gap: .5rem 1rem; flex-wrap: wrap; }
  .names li:first-child { border-top: none; }
  .more-link { display: inline-block; margin-top: .5rem; }
</style>
