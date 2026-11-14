<script lang="ts">
  import { api, setNeedLoginHandler, type AuthState } from './api'
  import { router, href } from './router.svelte'
  import { live, startLive, stopLive } from './live.svelte'
  import { between, worldName } from './format'
  import Days from './pages/Days.svelte'
  import Day from './pages/Day.svelte'
  import Visit from './pages/Visit.svelte'
  import Worlds from './pages/Worlds.svelte'
  import World from './pages/World.svelte'
  import People from './pages/People.svelte'
  import Person from './pages/Person.svelte'
  import Settings from './pages/Settings.svelte'
  import Photos from './pages/Photos.svelte'
  import Share from './pages/Share.svelte'
  import Login from './pages/Login.svelte'

  let auth = $state<AuthState | null>(null)
  // スマホの共有から来たとき（/?url=… など）。保存したら、アドレスから外す
  const shared = new URLSearchParams(location.search)
  let sharing = $state(shared.has('url') || shared.has('text'))
  if (shared.has('url') || shared.has('text')) history.replaceState(null, '', location.pathname + location.hash)
  // 共有の画面からほかの画面へ移ったら、共有の画面を閉じる
  window.addEventListener('hashchange', () => (sharing = false))
  let checked = $state(false)

  async function check() {
    try { auth = await api.authState() } catch { auth = null }
    checked = true
    if (auth?.allowed) startLive()
    else stopLive()
  }
  check()
  setNeedLoginHandler(() => { if (auth) auth = { ...auth, allowed: false }; stopLive() })

  // 「いま」の帯の経過時間を 1 分ごとに直す
  let now = $state(Date.now())
  $effect(() => {
    const t = setInterval(() => (now = Date.now()), 30000)
    return () => clearInterval(t)
  })

  const tabs = [
    { key: 'days', label: '日記', href: href.days(), pages: ['days', 'day', 'visit'] },
    { key: 'worlds', label: 'ワールド', href: href.worlds(), pages: ['worlds', 'world'] },
    { key: 'people', label: '人', href: href.people(), pages: ['people', 'person'] },
    { key: 'photos', label: '写真', href: href.photos(), pages: ['photos'] },
    { key: 'settings', label: '設定', href: href.settings(), pages: ['settings'] },
  ]
  const current = $derived(live.current)
  const peopleNow = $derived(current ? Object.keys(current.people).length : 0)
</script>

<header class="top">
  <div class="bar">
    <a class="brand" href={href.days()} aria-label="Yamaori の日記へ">
      <svg viewBox="0 0 32 32" width="22" height="22" aria-hidden="true"><path d="M4 9 16 4l12 5v14l-12 5-12-5z" fill="var(--accent-soft)" stroke="var(--accent)" stroke-width=".8" stroke-opacity=".5"/><path d="M16 4v24" stroke="var(--accent)" stroke-width="2"/><path d="M4 9l12 5 12-5" fill="none" stroke="var(--accent)" stroke-width="1.5" opacity=".6"/></svg>
      Yamaori
    </a>
    {#if auth?.allowed}
      <nav class="tabs" aria-label="画面">
        {#each tabs as t (t.key)}
          <a href={t.href} aria-current={!sharing && t.pages.includes(router.route.page) ? 'page' : undefined}>{t.label}</a>
        {/each}
      </nav>
    {/if}
  </div>
  {#if auth?.allowed && current}
    {@const _ = now}
    <a class="now" href={current.visitId ? href.visit(current.visitId) : href.days()}>
      <span class="dot" aria-hidden="true"></span>
      <span class="label">いま</span>
      <span class="world break">{current.worldName ?? current.worldId}</span>
      <span class="faint nowrap">{between(current.joinedAt, null)} · {peopleNow}人</span>
    </a>
  {/if}
</header>

<main>
  {#if !checked}
    <p class="faint center">読んでいます…</p>
  {:else if !auth}
    <p class="center error">Yamaori につながりません。Yamaori が動いているか確かめてください。</p>
  {:else if !auth.allowed}
    <Login {auth} onDone={check} />
  {:else}
    {#if sharing}
      <Share params={shared} onClose={() => (sharing = false)} />
    {:else}
    {#key router.route}
      {#if router.route.page === 'days'}<Days />
      {:else if router.route.page === 'day'}<Day date={router.route.date} />
      {:else if router.route.page === 'visit'}<Visit id={router.route.id} />
      {:else if router.route.page === 'worlds'}<Worlds />
      {:else if router.route.page === 'world'}<World id={router.route.id} />
      {:else if router.route.page === 'people'}<People />
      {:else if router.route.page === 'person'}<Person id={router.route.id} />
      {:else if router.route.page === 'photos'}<Photos />
      {:else if router.route.page === 'settings'}<Settings {auth} onLogout={check} />
      {/if}
    {/key}
    {/if}
  {/if}
</main>

<style>
  .top { position: sticky; top: 0; z-index: var(--z-bar); background: var(--bg); border-bottom: var(--border-width) solid var(--line); }
  .bar { max-width: 52rem; margin: 0 auto; padding: var(--space-2) var(--space-4); display: flex; align-items: center; gap: var(--space-4); }
  .brand { display: inline-flex; align-items: center; gap: var(--space-2); font-weight: var(--weight-bold); }
  .tabs { display: flex; gap: var(--space-1); margin-left: auto; overflow-x: auto; }
  .tabs a { padding: var(--space-2) var(--space-3); border-radius: var(--radius-full); color: var(--muted); white-space: nowrap; font-size: var(--font-small); }
  .tabs a:hover { background: var(--hover); color: var(--fg); }
  .tabs a[aria-current='page'] { background: var(--accent-soft); color: var(--accent-strong); font-weight: var(--weight-strong); }
  .now { max-width: 52rem; margin: 0 auto; padding: var(--space-1) var(--space-4) var(--space-2); display: flex; align-items: baseline; gap: var(--space-2); font-size: var(--font-small); }
  .now:hover { color: inherit; }
  .now:hover .world { color: var(--accent); }
  .now .label { color: var(--yamaori-live); font-weight: var(--weight-bold); }
  .now .world { font-weight: var(--weight-strong); min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .dot { width: 0.55rem; height: 0.55rem; border-radius: 50%; background: var(--yamaori-live); align-self: center; flex: none; animation: pulse 2s ease-in-out infinite; }
  @keyframes pulse { 50% { opacity: 0.35; } }
  @media (prefers-reduced-motion: reduce) { .dot { animation: none; } }
  main { max-width: 52rem; margin: 0 auto; padding: var(--space-5) var(--space-4) var(--space-10); }
  .center { text-align: center; padding: var(--space-10) 0; }
  .error { color: var(--danger); }
  @media (max-width: 480px) {
    .bar { flex-wrap: wrap; gap: var(--space-1) var(--space-3); }
    .tabs { margin-left: 0; width: 100%; }
    .tabs a { flex: 1; text-align: center; }
  }
</style>
