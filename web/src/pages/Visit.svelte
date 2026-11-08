<script lang="ts">
  import { untrack } from 'svelte'
  import { api, type VisitDetail } from '../api'
  import { accessLabel, between, dateTime, day, leftLabel, localDate, notificationLabel, safeHref, time, worldName } from '../format'
  import { href } from '../router.svelte'
  import { live } from '../live.svelte'
  import Notes from '../components/Notes.svelte'
  import PhotoGrid from '../components/PhotoGrid.svelte'
  import type { PhotoView } from '../api'
  import Loading from '../components/Loading.svelte'
  import { deviceLabel, loadDevices } from '../devices.svelte'

  let { id }: { id: string } = $props()
  let data = $state<VisitDetail | null>(null)
  let error = $state('')

  $effect(() => {
    live.version
    const target = id
    // 終わった訪問は、今の出来事が来ても読み直さない（data を見ても、この処理は走り直さない）
    const done = untrack(() => data && data.visit.id === target && data.visit.leftAt)
    if (done) return
    api.visit(target).then((d) => (data = d)).catch((e) => (error = (e as Error).message))
    api.photos({ visit: target, limit: 500 }).then((p) => (photos = p.items)).catch(() => {})
  })
  let photos = $state<PhotoView[]>([])
  loadDevices()

  const videos = $derived(data?.events.filter((e) => e.kind === 'video') ?? [])
  const shots = $derived(data?.events.filter((e) => e.kind === 'screenshot') ?? [])
  const notices = $derived(data?.events.filter((e) => e.kind === 'notification') ?? [])
  const avatars = $derived(data?.events.filter((e) => e.kind === 'avatar') ?? [])
  // 同じ人が出入りしたら 1 行にまとめる
  const people = $derived.by(() => {
    const m = new Map<string, { key: string; userId: string | null | undefined; name: string; spans: { joinedAt: string; leftAt: string | null | undefined }[] }>()
    for (const p of data?.people ?? []) {
      const key = p.userId ?? `name:${p.name}`
      const e = m.get(key) ?? { key, userId: p.userId, name: p.name, spans: [] }
      e.spans.push({ joinedAt: p.joinedAt, leftAt: p.leftAt })
      m.set(key, e)
    }
    return [...m.values()]
  })
</script>

{#if !data}
  <Loading {error} />
{:else}
  {@const v = data.visit}
  <div class="stack">
    <nav class="faint"><a href={href.day(localDate(v.joinedAt))}>← {day(localDate(v.joinedAt))}</a></nav>
    <header class="stack head">
      <h1 class="break"><a href={href.world(v.worldId)}>{worldName(v)}</a></h1>
      <div class="row">
        <span class="chip">{accessLabel(v.access)}</span>
        <span class="chip">#{v.instanceNumber} · {v.region.toUpperCase()}</span>
        {#if !v.leftAt}<span class="chip live">いる</span>{/if}
        {#if deviceLabel(v.deviceId)}<span class="chip" title="記録した端末">{deviceLabel(v.deviceId)}</span>{/if}
      </div>
      <p class="muted when">
        {dateTime(v.joinedAt)} – {v.leftAt ? time(v.leftAt) : 'いま'}（{between(v.joinedAt, v.leftAt)}）
        {#if leftLabel(v.leftReason)}<span class="faint">· 出た時刻は{leftLabel(v.leftReason)}</span>{/if}
      </p>
    </header>

    <Notes targetType="visit" targetId={v.id} notes={data.notes} />

    <section>
      <h2 class="section-title">いた人 <span class="faint">{people.length}</span></h2>
      {#if people.length}
        <ul class="people card">
          {#each people as p (p.key)}
            <li>
              <a href={href.person(p.userId ?? `name:${p.name}`)} class="break">{p.name}</a>
              <span class="faint nowrap">{p.spans.map((s) => `${time(s.joinedAt)}–${s.leftAt ? time(s.leftAt) : 'いま'}`).join(', ')}</span>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="faint">ほかにいた人はいません。</p>
      {/if}
    </section>

    {#if videos.length}
      <section>
        <h2 class="section-title">流れた動画 <span class="faint">{videos.length}</span></h2>
        <ul class="list card">
          {#each videos as e (e.id)}
            <li><span class="faint nowrap">{time(e.at)}</span>
              {#if safeHref(e.data.url)}<a class="break link" href={safeHref(e.data.url)} target="_blank" rel="noopener noreferrer">{e.data.url}</a>{:else}<span class="break">{e.data.url}</span>{/if}</li>
          {/each}
        </ul>
      </section>
    {/if}

    {#if photos.length}
      <section>
        <h2 class="section-title">撮った写真 <span class="faint">{photos.length}</span></h2>
        <PhotoGrid {photos} />
      </section>
    {:else if shots.length}
      <section>
        <h2 class="section-title">撮った写真 <span class="faint">{shots.length}</span></h2>
        <p class="faint">写真のフォルダにまだ見つかっていません（ログに残った置き場所）。</p>
        <ul class="list card">
          {#each shots as e (e.id)}
            <li><span class="faint nowrap">{time(e.at)}</span> <span class="break mono">{e.data.path}</span></li>
          {/each}
        </ul>
      </section>
    {/if}

    {#if notices.length}
      <section>
        <h2 class="section-title">通知 <span class="faint">{notices.length}</span></h2>
        <ul class="list card">
          {#each notices as e (e.id)}
            <li>
              <span class="faint nowrap">{time(e.at)}</span>
              {notificationLabel(e.data.type, e.data.direction)}
              {#if e.name}· {#if e.userId?.startsWith('usr_')}<a href={href.person(e.userId)}>{e.name}</a>{:else}{e.name}{/if}{/if}
              {#if e.data.message}<div class="faint break">{e.data.message}</div>{/if}
            </li>
          {/each}
        </ul>
      </section>
    {/if}

    {#if avatars.length}
      <section>
        <h2 class="section-title">着ていたアバター</h2>
        <p class="muted">{[...new Set(avatars.map((a) => a.data.avatar))].join('、')}</p>
      </section>
    {/if}

    <p class="faint mono break">{data.instance}</p>
  </div>
{/if}

<style>
  .head { gap: .5rem; }
  .when { margin: 0; font-variant-numeric: tabular-nums; }
  .people, .list { list-style: none; margin: 0; padding: .25rem 0; }
  .people li, .list li { padding: .45rem 1rem; border-top: 1px solid var(--line); display: flex; gap: .25rem .75rem; flex-wrap: wrap; align-items: baseline; }
  .people li:first-child, .list li:first-child { border-top: none; }
  .people li { justify-content: space-between; }
  .link { color: var(--accent); }
</style>
