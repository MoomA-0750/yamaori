<script lang="ts">
  import { api } from '../api'
  import { href } from '../router.svelte'

  // スマホの共有から来た（/?title=…&text=…&url=…）。中の wrld_… を保存の一覧に入れる
  let { params, onClose }: { params: URLSearchParams; onClose: () => void } = $props()
  // 開いただけでは保存しない（リンクを踏ませて勝手に保存させない）。中身を見せて、押したら保存する
  let status = $state<'ready' | 'saving' | 'saved' | 'none' | 'error'>('ready')
  let worldId = $state('')
  let error = $state('')
  const title = $derived(params.get('title') ?? '')

  const shared = $derived([params.get('url'), params.get('text')].filter(Boolean).join(' '))
  const found = $derived(/wrld_[0-9a-f-]{36}/i.exec(shared)?.[0] ?? '')

  async function save() {
    status = 'saving'
    try {
      const r = await api.saveWorld({ url: params.get('url'), text: params.get('text'), name: title || null })
      worldId = r.worldId
      status = 'saved'
    } catch (e) {
      status = /wrld_/.test((e as Error).message) ? 'none' : 'error'
      error = (e as Error).message
    }
  }
</script>

<div class="stack wrap">
  <div class="card box stack">
    {#if status === 'ready'}
      {#if found}
        <h1>ワールドを保存しますか</h1>
        <p class="muted break">{title || found}</p>
        <p class="faint mono break">{found}</p>
        <div class="row"><button class="primary" onclick={save}>保存する</button><button onclick={onClose}>やめる</button></div>
      {:else}
        <h1>ワールドが見つかりません</h1>
        <p class="muted">共有した中に、vrchat.com のワールドのアドレス（wrld_…）がありませんでした。</p>
      {/if}
    {:else if status === 'saving'}
      <p>保存しています…</p>
    {:else if status === 'saved'}
      <h1>保存しました</h1>
      <p class="muted break">{title || worldId}</p>
      <p class="faint">詳しい情報（説明・人数の上限など）は、PC の拡張機能か「まとめて取る」で埋まります。</p>
      <div class="row">
        <a class="chip accent" href={href.world(worldId)}>このワールドを開く →</a>
        <a class="chip accent" href={href.worlds() + '?list=*'}>保存したワールド →</a>
      </div>
    {:else if status === 'none'}
      <h1>ワールドが見つかりません</h1>
      <p class="muted">共有した中に、vrchat.com のワールドのアドレス（wrld_…）がありませんでした。</p>
    {:else}
      <h1>保存できませんでした</h1>
      <p class="error">{error}</p>
    {/if}
    {#if status !== 'saving' && !(status === 'ready' && found)}<div><button onclick={onClose}>Yamaori を開く</button></div>{/if}
  </div>
</div>

<style>
  .wrap { min-height: 50vh; display: grid; place-items: center; }
  .box { width: min(100%, 26rem); padding: 1.4rem; gap: .6rem; }
  .box p { margin: 0; }
  .error { color: var(--danger); }
</style>
