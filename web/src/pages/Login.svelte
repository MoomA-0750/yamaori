<script lang="ts">
  import { api, ApiError, type AuthState } from '../api'

  let { auth, onDone }: { auth: AuthState | null; onDone: () => void } = $props()
  let password = $state('')
  let error = $state('')
  let busy = $state(false)

  async function submit(e: SubmitEvent) {
    e.preventDefault()
    busy = true; error = ''
    try {
      await api.login(password)
      password = ''
      onDone()
    } catch (err) {
      error = err instanceof ApiError && err.status === 429 ? err.message : 'パスワードが違います'
    }
    busy = false
  }
</script>

<div class="wrap">
  <form class="card box stack" onsubmit={submit}>
    <input type="text" name="username" autocomplete="username" value="yamaori" hidden readonly />
    <h1>Yamaori</h1>
    {#if auth?.reason === 'NeedPassword'}
      <p>ほかの端末から見るには、先に Yamaori を動かしている PC でパスワードを決めてください。</p>
      <p class="faint">その PC のブラウザで http://localhost:47800 を開き、設定から決められます。</p>
    {:else if auth?.reason === 'TailscaleDenied'}
      <p>この Tailscale のアカウント（{auth.tailscaleLogin}）は、見てよい人に入っていません。</p>
      <p class="faint">Yamaori の設定で、見てよい Tailscale のアカウントに足すか、パスワードでログインしてください。</p>
      <label class="stack field">パスワード<input type="password" autocomplete="current-password" bind:value={password} /></label>
      <button class="primary" disabled={busy || !password}>ログイン</button>
    {:else}
      <label class="stack field">パスワード<input type="password" autocomplete="current-password" bind:value={password} /></label>
      <button class="primary" disabled={busy || !password}>ログイン</button>
    {/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}
  </form>
</div>

<style>
  .wrap { min-height: 70vh; display: grid; place-items: center; }
  .box { width: min(100%, 24rem); padding: 1.5rem; gap: .9rem; }
  .box p { margin: 0; }
  .field { gap: .3rem; }
  .error { color: var(--danger); }
</style>
