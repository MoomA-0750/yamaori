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
  .top { position: sticky; top: 0; z-index: 10; background: color-mix(in srgb, var(--bg) 88%, transparent); backdrop-filter: blur(10px); border-bottom: 1px solid var(--line); }
  .bar { max-width: 52rem; margin: 0 auto; padding: .5rem 1rem; display: flex; align-items: center; gap: 1rem; }
  .brand { display: inline-flex; align-items: center; gap: .45rem; font-weight: 700; letter-spacing: .02em; }
  .tabs { display: flex; gap: .15rem; margin-left: auto; overflow-x: auto; }
  .tabs a { padding: .4rem .75rem; border-radius: 999px; color: var(--ink-2); white-space: nowrap; }
  .tabs a:hover { background: var(--surface-2); color: var(--ink); }
  .tabs a[aria-current='page'] { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
  .now { max-width: 52rem; margin: 0 auto; padding: .35rem 1rem .55rem; display: flex; align-items: baseline; gap: .5rem; font-size: .9rem; }
  .now:hover { color: inherit; }
  .now:hover .world { color: var(--accent); }
  .now .label { color: var(--live); font-weight: 700; }
  .now .world { font-weight: 600; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .dot { width: .55rem; height: .55rem; border-radius: 50%; background: var(--live); align-self: center; flex: none; animation: pulse 2s ease-in-out infinite; }
  @keyframes pulse { 50% { opacity: .35; } }
  main { max-width: 52rem; margin: 0 auto; padding: 1.25rem 1rem 4rem; }
  .center { text-align: center; padding: 3rem 0; }
  .error { color: var(--danger); }
  @media (max-width: 480px) {
    .bar { flex-wrap: wrap; gap: .35rem .75rem; }
    .tabs { margin-left: 0; width: 100%; }
    .tabs a { flex: 1; text-align: center; }
  }
</style>
