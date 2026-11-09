<script lang="ts">
  import { api } from '../api'
  import type { components } from '../schema'
  import { dateTime } from '../format'
  type S = components['schemas']

  // 端末のグループ: まとめ役は記録を全部持って画面を配る。ほかの端末は役（ログ・写真・VRCX）に従って集めて送る
  let { onrestart, legacyChild = $bindable(false) }: { onrestart: () => void; legacyChild?: boolean } = $props()
  let view = $state<S['GroupView'] | null>(null)
  let msg = $state('')
  let name = $state('自分の端末')
  let invite = $state<S['GroupInvite'] | null>(null)
  let hubUrl = $state('')
  let code = $state('')
  let found = $state<S['DiscoveryResult'] | null>(null)
  let searching = $state(false)
  let needRestart = $state(false)

  const roleLabels: Record<string, string> = { logs: 'ログを集める', photos: '写真を扱う', vrcx: 'VRCX と連携する' }
  const editable = ['logs', 'photos', 'vrcx']

  async function load() {
    try { view = await api.group(); legacyChild = view.mode === 'child' } catch (e) { msg = (e as Error).message }
  }
  load()

  async function create() {
    msg = ''
    try { view = await api.createGroup(name.trim() || '自分の端末'); msg = 'グループを作りました。招待の合言葉を作って、ほかの端末で入れます' } catch (e) { msg = (e as Error).message }
  }
  async function makeInvite() { try { invite = await api.groupInvite() } catch (e) { msg = (e as Error).message } }
  async function toggle(m: S['GroupMember'], role: string) {
    const roles = m.roles.includes(role) ? m.roles.filter((r) => r !== role) : [...m.roles, role]
    try { view = await api.setMemberRoles(m.deviceId, roles) } catch (e) { msg = (e as Error).message }
  }
  async function remove(m: S['GroupMember']) {
    try { view = await api.removeMember(m.deviceId); msg = `${m.name} を外しました（送ってきた記録は残ります）` } catch (e) { msg = (e as Error).message }
  }
  async function discover() {
    searching = true; found = null
    try { found = await api.discover() } catch (e) { msg = (e as Error).message }
    searching = false
  }
  async function join() {
    msg = 'まとめ役につないでいます…'
    try {
      const r = await api.joinGroup(hubUrl.trim(), code.trim())
      msg = r.message
      if (r.ok) { needRestart = r.restartRequired; await load() }
    } catch (e) { msg = (e as Error).message }
  }
  async function leave() {
    try { const r = await api.leaveGroup(); msg = r.message; needRestart = r.restartRequired; await load() } catch (e) { msg = (e as Error).message }
  }
  const hubUrlHint = $derived(location.origin)
</script>

{#if view}
  <section class="card pad stack">
    <h2>端末のグループ</h2>
    {#if view.mode === 'hub' && view.group}
      <p class="muted"><b>{view.group.name}</b> のまとめ役です。記録は全部この端末に集まり、ほかの端末は役に従って集めて送ります。</p>
      <ul class="members">
        {#each view.group.members as m (m.deviceId)}
          <li class="member">
            <div class="who">
              <b class="break">{m.name}</b>
              {#if m.roles.includes('hub')}<span class="chip accent">まとめ役</span>{/if}
              {#if m.isSelf}<span class="chip">この端末</span>{/if}
              <span class="faint nums">{m.lastSeenAt ? `最後の連絡 ${dateTime(m.lastSeenAt)}` : m.isSelf ? '' : 'まだ連絡がない'}</span>
            </div>
            <div class="roles">
              {#each editable as r (r)}
                <label class="check"><input type="checkbox" checked={m.roles.includes(r)} onchange={() => toggle(m, r)} /> {roleLabels[r]}</label>
              {/each}
              {#if !m.roles.includes('hub')}<button class="danger-text" onclick={() => remove(m)}>外す</button>{/if}
            </div>
          </li>
        {/each}
      </ul>
      <div class="stack">
        <div><button onclick={makeInvite}>招待の合言葉を作る</button></div>
        {#if invite}
          <div class="invite">
            <span class="faint">入れたい端末の Yamaori の設定で、まとめ役のアドレスとこの合言葉を入れます（一度きり・{dateTime(invite.expiresAt)} まで）</span>
            <code class="code">{invite.code}</code>
            <span class="faint">まとめ役のアドレス: <code>{hubUrlHint}</code>（Tailscale なら、ほかの端末の「探す」で出ます）</span>
          </div>
        {/if}
      </div>
    {:else if view.mode === 'member' && view.member}
      <p class="muted"><b>{view.member.groupName}</b> に入っています。記録はまとめ役に集まるので、まとめ役の画面で見ます。</p>
      <div class="row"><a class="chip accent" href={view.member.hubUrl} target="_blank" rel="noopener">まとめ役の画面を開く ↗</a><span class="faint break">{view.member.hubUrl}</span></div>
      <p class="faint">この端末の役: {view.member.roles.length ? view.member.roles.map((r) => roleLabels[r] ?? r).join('・') : 'なし'}（まとめ役の画面で変えます）</p>
      {#if !view.member.tokenReadable}<p class="warn">まとめ役のトークンを読めません。抜けてから、もう一度入ってください。</p>{/if}
      <div><button class="danger-text" onclick={leave}>グループから抜ける</button></div>
    {:else}
      {#if view.mode === 'child'}<p class="faint">今は前の形（子の端末）で送っています。グループに入り直すと、役を決められます。</p>{/if}
      <div class="two">
        <div class="stack">
          <h3>グループを作る</h3>
          <p class="faint">この端末を「まとめ役」にします。記録を全部持ち、画面を配る端末です（いつも動いている PC や NAS に向いています）。</p>
          <form class="row" onsubmit={(e) => { e.preventDefault(); create() }}>
            <input type="text" bind:value={name} aria-label="グループの名前" maxlength="100" />
            <button class="primary" type="submit">作る</button>
          </form>
        </div>
        <div class="stack">
          <h3>グループに入る</h3>
          <div><button onclick={discover} disabled={searching}>{searching ? '探しています…' : 'Tailscale でまとめ役を探す'}</button></div>
          {#if found}
            {#if !found.tailscaleAvailable}
              <p class="faint">{found.problem}。アドレスを入れてください。</p>
            {:else if found.found.length === 0}
              <p class="faint">見つかりませんでした（まとめ役の Yamaori が新しい版で、Tailscale から見える必要があります）。</p>
            {:else}
              <ul class="plain">
                {#each found.found as f (f.url)}
                  <li><button class="pick" onclick={() => (hubUrl = f.url)}>
                    <span><b>{f.deviceName}</b>{#if f.isHub} <span class="chip accent">まとめ役: {f.groupName}</span>{/if}</span>
                    <span class="faint break">{f.url}</span>
                  </button></li>
                {/each}
              </ul>
            {/if}
          {/if}
          <label class="stack field">まとめ役のアドレス<input type="text" bind:value={hubUrl} placeholder="https://truenas.example.ts.net" /></label>
          <label class="stack field">招待の合言葉<input type="text" bind:value={code} placeholder="ABCDE-FGHJK" autocomplete="off" /></label>
          <div><button class="primary" onclick={join} disabled={!hubUrl.trim() || !code.trim()}>入る</button></div>
        </div>
      </div>
    {/if}
    {#if msg}<p class="muted" role="status">{msg}</p>{/if}
    {#if needRestart}<div><button onclick={onrestart}>Yamaori を起こし直す</button></div>{/if}
  </section>
{/if}

<style>
  h3 { margin: 0; font-size: 1rem; }
  p { margin: 0; }
  .members { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .5rem; }
  .member { border: 1px solid var(--line); border-radius: var(--radius-sm); padding: .6rem .75rem; display: flex; flex-direction: column; gap: .4rem; }
  .who { display: flex; gap: .5rem; align-items: baseline; flex-wrap: wrap; }
  .roles { display: flex; gap: .35rem 1rem; flex-wrap: wrap; align-items: center; }
  .invite { display: flex; flex-direction: column; gap: .35rem; background: var(--accent-soft); border-radius: var(--radius-sm); padding: .75rem; }
  .invite .faint { color: var(--ink-2); }
  .code { font-size: 1.5rem; letter-spacing: .15em; font-weight: 700; }
  .two { display: grid; grid-template-columns: 1fr 1fr; gap: 1.25rem; }
  .plain { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .25rem; }
  .pick { width: 100%; display: flex; flex-direction: column; align-items: flex-start; gap: .1rem; text-align: left; }
  form input { flex: 1; min-width: 0; }
  .warn, .danger-text { color: var(--danger, #b3261e); }
  @media (max-width: 640px) { .two { grid-template-columns: 1fr; } }
</style>
