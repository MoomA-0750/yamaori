<script lang="ts">
  import { api, ApiError, type AccountView, type ApiToken, type AuthState, type DeviceView, type SettingsView, type TokenScope } from '../api'
  import { dateTime } from '../format'

  let { auth, onLogout }: { auth: AuthState | null; onLogout: () => void } = $props()

  let settings = $state<SettingsView | null>(null)
  let tokens = $state<ApiToken[]>([])
  let forgotten = $state<{ key: string; label?: string | null; forgottenAt: string }[]>([])
  async function unforget(key: string) {
    await api.unforget(key)
    forgotten = await api.forgotten()
  }
  let devices = $state<DeviceView[]>([])
  let accounts = $state<AccountView[]>([])
  let error = $state('')

  // 見せ方
  let listen = $state('local')
  let port = $state(47800)
  let deviceName = $state('')
  let tailscaleLogins = $state('')
  let localRequiresLogin = $state(false)
  let openRemote = $state(false)
  let confirmOpen = $state(false)
  let savedMsg = $state('')
  let restartNeeded = $state(false)

  async function load() {
    try {
      const s = await api.settings()
      settings = s
      listen = s.listen; port = s.port; deviceName = s.deviceName
      tailscaleLogins = s.tailscaleLogins.join('\n')
      localRequiresLogin = s.localRequiresLogin
      openRemote = s.allowUnauthenticatedRemote
      ;[tokens, devices, accounts, forgotten] = await Promise.all([api.tokens(), api.devices(), api.accounts(), api.forgotten()])
    } catch (e) { error = (e as Error).message }
  }
  load()

  async function save() {
    savedMsg = ''; error = ''
    try {
      const r = await api.saveSettings({
        listen, port, deviceName,
        tailscaleLogins: tailscaleLogins.split(/[\s,]+/).filter(Boolean),
        localRequiresLogin,
        allowUnauthenticatedRemote: openRemote,
        confirmUnauthenticatedRemote: openRemote ? confirmOpen : null,
      })
      settings = r.settings
      restartNeeded = r.restartRequired
      savedMsg = r.restartRequired ? '保存しました。見せ方・番号・端末の名前は、起こし直すと効きます。' : '保存しました。'
    } catch (e) { error = e instanceof ApiError ? e.message : (e as Error).message }
  }

  let restarting = $state(false)
  async function restart() {
    restarting = true
    try { await api.restart() } catch { /* 止まる途中で切れることがある */ }
    const url = listen === 'local' || port === settings?.port ? location.href : null
    setTimeout(() => { if (url) location.reload() }, 4000)
  }

  // パスワード
  let current = $state('')
  let next = $state('')
  let next2 = $state('')
  let pwMsg = $state('')
  async function setPassword(e: SubmitEvent) {
    e.preventDefault()
    pwMsg = ''
    if (next !== next2) { pwMsg = '確かめの入力が合いません'; return }
    try {
      await api.setPassword(settings?.passwordSet ? current : null, next)
      current = next = next2 = ''
      pwMsg = 'パスワードを決めました。ほかの端末のログインは切れました。'
      await load()
    } catch (e) { pwMsg = e instanceof ApiError && e.status === 401 ? '今のパスワードが違います' : (e as Error).message }
  }

  // API のトークン
  let tokenName = $state('')
  let tokenScope = $state<TokenScope>('extension')
  const scopeLabels: Record<string, string> = { full: '全部（設定は除く）', extension: '拡張機能・スマホの保存', sync: '子の端末' }
  let newSecret = $state('')
  async function createToken() {
    if (!tokenName.trim()) return
    const r = await api.createToken(tokenName.trim(), tokenScope)
    newSecret = r.secret
    tokenName = ''
    tokens = await api.tokens()
  }
  async function deleteToken(id: string) {
    await api.deleteToken(id)
    tokens = await api.tokens()
  }

  // 写真
  let photo = $state<import('../api').PhotoSettingsView | null>(null)
  let photoDirs = $state('')
  let kind = $state('none')
  let target = $state('')
  let username = $state('')
  let password = $state('')
  let retention = $state('keep')
  let retentionDays = $state(30)
  let photoMsg = $state('')
  let testMsg = $state('')
  let confirmDelete = $state(false)
  api.photoSettings().then((p) => {
    photo = p
    photoDirs = p.photoDirs.join('\n'); kind = p.kind; target = p.target; username = p.username ?? ''
    retention = p.retention; retentionDays = p.retentionDays
  }).catch(() => {})
  const photoInput = () => ({ photoDirs: photoDirs.split('\n').map((x) => x.trim()).filter(Boolean), kind, target, username: username || null, password: password || null, retention, retentionDays })
  async function savePhoto() {
    photoMsg = ''
    if (retention !== 'keep' && kind !== 'none' && !confirmDelete) { photoMsg = '手元の写真を消す設定にするには、下の確かめに印を付けてください'; return }
    try {
      photo = await api.savePhotoSettings(photoInput())
      password = ''
      photoMsg = '保存しました。起こし直すと効きます。'
      restartNeeded = true
    } catch (e) { photoMsg = (e as Error).message }
  }
  async function testTarget() {
    testMsg = 'つないでいます…'
    try { const r = await api.testPhotoTarget(photoInput()); testMsg = r.ok ? 'つながりました' : `つながりません: ${r.message}` } catch (e) { testMsg = (e as Error).message }
  }

  // 端末の役割
  let sync = $state<import('./../schema').components['schemas']['SyncSettingsView'] | null>(null)
  let role = $state('standalone')
  let parentUrl = $state('')
  let parentToken = $state('')
  let syncMsg = $state('')
  api.syncSettings().then((x) => { sync = x; role = x.role; parentUrl = x.parentUrl ?? '' }).catch(() => {})
  async function saveSync() {
    syncMsg = ''
    try {
      sync = await api.saveSyncSettings({ role, parentUrl, parentToken: parentToken || null })
      parentToken = ''
      syncMsg = '保存しました。起こし直すと効きます。'
      restartNeeded = true
    } catch (e) { syncMsg = (e as Error).message }
  }
  async function testParent() {
    syncMsg = 'つないでいます…'
    try { const r = await api.testParent({ parentUrl, parentToken: parentToken || null }); syncMsg = r.ok ? r.message : `つながりません: ${r.message}` } catch (e) { syncMsg = (e as Error).message }
  }

  async function logout() {
    await api.logout()
    onLogout()
  }
</script>

<div class="stack">
  <h1>設定</h1>
  {#if error}<p class="error" role="alert">{error}</p>{/if}

  {#if settings}
    <section class="card pad stack">
      <h2>見せ方</h2>
      <fieldset class="stack choices">
        <legend class="sr">見せ方</legend>
        <label class="choice"><input type="radio" bind:group={listen} value="local" />
          <span><b>この PC だけ</b><span class="faint">ほかの端末からは見えません（既定）</span></span></label>
        <label class="choice"><input type="radio" bind:group={listen} value="lan" />
          <span><b>家の中の端末からも</b><span class="faint">同じネットワークのスマホなどから http://この PC のアドレス:{port} で見ます。パスワードが要ります</span></span></label>
        <label class="choice"><input type="radio" bind:group={listen} value="tailscale" />
          <span><b>Tailscale から</b><span class="faint">この PC で <code>tailscale serve {port}</code> を動かし、下の Tailscale のアカウントだけを通します</span></span></label>
      </fieldset>

      {#if listen === 'lan' && !settings.passwordSet && !openRemote}
        <p class="warn">家の中から見せるには、先に下でパスワードを決めてください。</p>
      {/if}

      {#if listen === 'tailscale'}
        <label class="stack field">見てよい Tailscale のアカウント（1 行に 1 つ）
          <textarea rows="2" bind:value={tailscaleLogins} placeholder="me@example.com"></textarea></label>
      {/if}

      <div class="grid2">
        <label class="stack field">番号<input type="number" min="1024" max="65535" bind:value={port} /></label>
        <label class="stack field">この端末の名前<input type="text" bind:value={deviceName} /></label>
      </div>

      <label class="check"><input type="checkbox" bind:checked={localRequiresLogin} disabled={!settings.passwordSet} />
        この PC から開くときもログインを求める</label>

      <details class="danger-zone" open={openRemote}>
        <summary>認証なしで外から見せる（勧めません）</summary>
        <p class="faint">自分で公開したアドレスから、だれでもログインなしで記録（会った人の名前を含む）を読めるようになります（書き換え・設定・止めることは、これまでどおりログインが要ります）。公開するときは、パスワードか Tailscale を使ってください。</p>
        <label class="check"><input type="checkbox" bind:checked={openRemote} /> 認証なしで外から見せる</label>
        {#if openRemote && !settings.allowUnauthenticatedRemote}
          <label class="check"><input type="checkbox" bind:checked={confirmOpen} /> 記録がだれにでも見えることを分かった上で開く</label>
        {/if}
      </details>

      <div class="row">
        <button class="primary" onclick={save}>保存</button>
        {#if restartNeeded}<button onclick={restart} disabled={restarting}>{restarting ? '起こし直しています…' : 'Yamaori を起こし直す'}</button>{/if}
      </div>
      {#if savedMsg}<p class="ok">{savedMsg}</p>{/if}
    </section>

    {#if photo}
      <section class="card pad stack">
        <h2>写真</h2>
        <label class="stack field">見張るフォルダ（1 行に 1 つ。空なら VRChat の写真のフォルダ）
          <textarea rows="2" bind:value={photoDirs} placeholder={photo.effectivePhotoDirs.join('\n')}></textarea></label>
        <label class="stack field">送り先
          <select bind:value={kind}>
            <option value="none">送らない</option>
            <option value="folder">フォルダ（NAS の共有など）</option>
            <option value="webdav">WebDAV</option>
            <option value="sftp">SFTP</option>
          </select></label>
        {#if kind !== 'none'}
          <p class="faint">{kind === 'folder' ? 'NAS の共有（\\\\NAS\\photo\\VRChat）や、割り当てたドライブ（Z:\\VRChat）。' : kind === 'webdav' ? 'https:// で始まる WebDAV のアドレス（Synology・QNAP・Nextcloud など）。' : 'sftp://ホスト/フォルダ の形。最初につないだときの相手の鍵を覚え、鍵が変わったら送らない（相手の鍵を変えたときは、ここで保存し直す）。'}</p>
          <label class="stack field">送り先のアドレス<input type="text" bind:value={target} placeholder={kind === 'folder' ? '\\\\NAS\\photo\\VRChat' : kind === 'webdav' ? 'https://nas.local:5006/photo/VRChat' : 'sftp://nas.local/volume1/photo/VRChat'} /></label>
          {#if kind !== 'folder'}
            <div class="grid2">
              <label class="stack field">ユーザー名<input type="text" bind:value={username} autocomplete="off" /></label>
              <label class="stack field">パスワード{photo.passwordSet ? '（決めてあります。変えるときだけ入れる）' : ''}<input type="password" bind:value={password} autocomplete="new-password" /></label>
            </div>
          {/if}
          <div class="row"><button onclick={testTarget}>試しにつなぐ</button><span class="faint">{testMsg}</span></div>
          <fieldset class="stack choices">
            <legend class="faint">送ったあと、この PC の写真を</legend>
            <label class="choice"><input type="radio" bind:group={retention} value="keep" /><span>全部残す（既定）</span></label>
            <label class="choice"><input type="radio" bind:group={retention} value="days" /><span class="inline">撮ってから <input class="days" type="number" min="1" bind:value={retentionDays} /> 日たったら消す</span></label>
            <label class="choice"><input type="radio" bind:group={retention} value="after_upload" /><span>送ったら消す</span></label>
          </fieldset>
          {#if retention !== 'keep'}
            <label class="check"><input type="checkbox" bind:checked={confirmDelete} /> 送り先に同じ大きさで届いたと確かめた写真だけを、この PC から消すことを分かった上で決める</label>
          {/if}
          <p class="faint">送り先には「年-月/ファイル名」で置きます。10 分ごとに送ります。消す前に縮小を作るので、消したあとも Yamaori で見られます。</p>
        {/if}
        <div class="row"><button class="primary" onclick={savePhoto}>保存</button>{#if restartNeeded}<button onclick={restart} disabled={restarting}>{restarting ? '起こし直しています…' : 'Yamaori を起こし直す'}</button>{/if}</div>
        {#if photoMsg}<p class="muted">{photoMsg}</p>{/if}
      </section>
    {/if}

    {#if sync}
      <section class="card pad stack">
        <h2>ほかの端末と</h2>
        <fieldset class="stack choices">
          <legend class="sr">この端末の役割</legend>
          <label class="choice"><input type="radio" bind:group={role} value="standalone" />
            <span><b>ひとりで（親にもなれる）</b><span class="faint">記録をこの端末に持ちます。ほかの端末（子）からの記録も受け取れます</span></span></label>
          <label class="choice"><input type="radio" bind:group={role} value="child" />
            <span><b>子</b><span class="faint">この端末でも記録し、親の端末へ 10 分ごとに送ります（ノートなど）。親につながらない間は、つながったときにまとめて送ります</span></span></label>
        </fieldset>
        {#if role === 'child'}
          <label class="stack field">親のアドレス<input type="text" bind:value={parentUrl} placeholder="http://desktop:47800" /></label>
          <label class="stack field">親で作ったトークン{sync.parentTokenSet ? '（決めてあります。変えるときだけ入れる）' : ''}<input type="password" bind:value={parentToken} autocomplete="off" /></label>
          <p class="faint">親の端末では、見せ方を「家の中の端末からも」か「Tailscale から」にし、設定の「ほかのアプリ用のトークン」で、範囲を「子の端末」にしてこの端末用のトークンを作ります。</p>
        {:else}
          <p class="faint">この端末を親にするときは、見せ方を「家の中の端末からも」か「Tailscale から」にし、子の端末ごとに範囲が「子の端末」のトークンを作って渡します。</p>
        {/if}
        <div class="row">
          {#if role === 'child'}<button onclick={testParent}>試しにつなぐ</button>{/if}
          <button class="primary" onclick={saveSync}>保存</button>
          {#if restartNeeded}<button onclick={restart} disabled={restarting}>{restarting ? '起こし直しています…' : 'Yamaori を起こし直す'}</button>{/if}
        </div>
        {#if syncMsg}<p class="muted">{syncMsg}</p>{/if}
      </section>
    {/if}

    <section class="card pad stack">
      <h2>パスワード</h2>
      <p class="faint">{settings.passwordSet ? '決めてあります。' : 'まだ決めていません。この PC からはパスワードなしで見られます。'}ほかの端末から見るときに使います。</p>
      <form class="stack" onsubmit={setPassword}>
        <input type="text" name="username" autocomplete="username" value="yamaori" hidden readonly />
        {#if settings.passwordSet}
          <label class="stack field">今のパスワード<input type="password" autocomplete="current-password" bind:value={current} /></label>
        {/if}
        <div class="grid2">
          <label class="stack field">新しいパスワード（8 文字以上）<input type="password" autocomplete="new-password" bind:value={next} /></label>
          <label class="stack field">もう一度<input type="password" autocomplete="new-password" bind:value={next2} /></label>
        </div>
        <div class="row"><button class="primary" disabled={next.length < 8}>{settings.passwordSet ? '変える' : '決める'}</button></div>
        {#if pwMsg}<p class="muted">{pwMsg}</p>{/if}
      </form>
    </section>

    <section class="card pad stack">
      <h2>ほかのアプリ用のトークン</h2>
      <p class="faint">ほかのアプリから API（/api/…）を使うときに、<code>Authorization: Bearer トークン</code> で渡します。API の定義は <a href="/openapi/v1.json" target="_blank">/openapi/v1.json</a>。
        渡す先に要る範囲だけにしてください。どのトークンでも、設定・トークン・パスワード・止めることには触れません（ログインした画面かこの PC からだけ）。子の端末のトークンは、最初に送ってきた端末に結び付きます。</p>
      {#if tokens.length}
        <ul class="tokens">
          {#each tokens as t (t.id)}
            <li class="spread"><span><b>{t.name}</b> <span class="chip">{scopeLabels[t.scope ?? 'full'] ?? t.scope}</span> <span class="faint">作成 {dateTime(t.createdAt)}{t.lastUsedAt ? ` · 最後に使った ${dateTime(t.lastUsedAt)}` : ''}</span></span>
              <button class="ghost danger" onclick={() => deleteToken(t.id)}>消す</button></li>
          {/each}
        </ul>
      {/if}
      <div class="row">
        <input type="text" placeholder="名前（どのアプリに渡すか）" bind:value={tokenName} class="grow" />
        <select bind:value={tokenScope} class="scope" aria-label="範囲">
          <option value="extension">{scopeLabels.extension}</option>
          <option value="sync">{scopeLabels.sync}</option>
          <option value="full">{scopeLabels.full}</option>
        </select>
        <button onclick={createToken} disabled={!tokenName.trim()}>作る</button>
      </div>
      {#if newSecret}
        <div class="secret">
          <p>このトークンは今しか表示しません。控えてください。</p>
          <code class="break">{newSecret}</code>
        </div>
      {/if}
    </section>

    <section class="card pad stack">
      <h2>端末とアカウント</h2>
      <ul class="plain">
        {#each devices as d (d.id)}
          <li>{d.name} {#if d.isSelf}<span class="chip">この端末</span>{/if} <span class="faint">訪問 {d.visitCount}</span></li>
        {/each}
      </ul>
      <ul class="plain">
        {#each accounts as a (a.id)}
          <li>{a.name ?? a.id} <span class="faint">訪問 {a.visitCount}{a.names.length > 1 ? ` · 前の名前 ${a.names.slice(0, -1).map((n) => n.name).join('、')}` : ''}</span></li>
        {/each}
      </ul>
    </section>

    {#if forgotten.length}
      <section class="card pad stack">
        <h2>忘れた人</h2>
        <p class="faint">この人たちの記録は入れません。忘れるのをやめると、次にログを読み直す・取り込み直すときに記録が戻ります（消したメモは戻りません）。</p>
        <ul class="tokens">
          {#each forgotten as f (f.key)}
            <li class="spread"><span><b>{f.label ?? f.key}</b> <span class="faint">{dateTime(f.forgottenAt)}</span></span>
              <button class="ghost" onclick={() => unforget(f.key)}>忘れるのをやめる</button></li>
          {/each}
        </ul>
      </section>
    {/if}

    {#if auth?.loggedIn}
      <div><button onclick={logout}>ログアウト</button></div>
    {/if}
  {/if}
</div>

<style>
  .pad { padding: 1.1rem 1.2rem; gap: .8rem; }
  .choices { border: 0; padding: 0; margin: 0; gap: .5rem; }
  .choice { display: flex; gap: .6rem; align-items: flex-start; cursor: pointer; }
  .choice input { margin-top: .35rem; }
  .choice > span { display: flex; flex-direction: column; }
  .field { gap: .25rem; font-size: .9rem; }
  .check { display: flex; gap: .5rem; align-items: center; }
  .grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: .6rem; }
  .warn { color: var(--danger); margin: 0; }
  .ok { color: var(--live); margin: 0; }
  .error { color: var(--danger); }
  .danger-zone { border: 1px dashed var(--line); border-radius: var(--radius-sm); padding: .6rem .8rem; }
  .danger-zone summary { cursor: pointer; color: var(--ink-2); }
  .danger-zone p { margin: .5rem 0; }
  .scope { width: auto; }
  .tokens, .plain { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .35rem; }
  .grow { flex: 1; min-width: 10rem; width: auto; }
  .choice > span.inline { display: inline; }
  .days { width: 4.5rem; display: inline-block; min-height: 2rem; padding: .2rem .4rem; }
  .secret { background: var(--accent-soft); border-radius: var(--radius-sm); padding: .7rem .9rem; }
  .secret p { margin: 0 0 .3rem; }
  .sr { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
  code { font-family: var(--mono); font-size: .88em; }
  @media (max-width: 480px) { .grid2 { grid-template-columns: 1fr; } }
</style>
