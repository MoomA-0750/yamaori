<script lang="ts">
  import { api } from '../api'
  import { settled } from '../dialog'
  import type { components } from '../schema'

  // 写真をアルバムに入れる。開くと、アルバムの一覧と、新しいアルバムの名前の欄が出る
  let { photoIds, onclose, ondone }: { photoIds: string[]; onclose: () => void; ondone?: (msg: string) => void } = $props()
  let albums = $state<components['schemas']['Album'][] | null>(null)
  let name = $state('')
  let msg = $state('')
  let dialog = $state<HTMLDialogElement>()
  api.albums().then((a) => (albums = a)).catch((e) => (msg = (e as Error).message))
  $effect(() => { dialog?.showModal() })

  async function put(id: string, label: string) {
    try {
      const r = await api.addToAlbum(id, photoIds)
      ondone?.(`「${label}」に ${r.count} 枚入れました`)
      dialog?.close()
    } catch (e) { msg = (e as Error).message }
  }
  async function create() {
    if (!name.trim()) return
    try { const a = await api.createAlbum(name.trim()); await put(a.id, a.name) } catch (e) { msg = (e as Error).message }
  }
</script>

<dialog class="k-dialog" bind:this={dialog} onclose={() => settled(dialog).then(onclose)} aria-label="アルバムに入れる">
  <div class="stack">
    <h2 class="k-dialog__title">{photoIds.length} 枚をアルバムに入れる</h2>
    {#if albums?.length}
      <ul class="list">
        {#each albums as a (a.id)}
          <li><button class="pick" onclick={() => put(a.id, a.name)}><span class="break">{a.name}</span><span class="faint nums">{a.count} 枚</span></button></li>
        {/each}
      </ul>
    {:else if albums}
      <p class="faint">まだアルバムがありません。</p>
    {/if}
    <form class="row" onsubmit={(e) => { e.preventDefault(); create() }}>
      <input type="text" bind:value={name} placeholder="新しいアルバムの名前" aria-label="新しいアルバムの名前" maxlength="200" />
      <button class="primary" type="submit" disabled={!name.trim()}>作って入れる</button>
    </form>
    {#if msg}<p class="error">{msg}</p>{/if}
  </div>
  <div class="k-dialog__actions"><button onclick={() => dialog?.close()}>やめる</button></div>
</dialog>

<style>
  .k-dialog__title { margin: 0; }
  .list { list-style: none; margin: 0; padding: 0; max-height: 45vh; overflow: auto; display: flex; flex-direction: column; gap: var(--space-1); }
  .pick { width: 100%; display: flex; justify-content: space-between; gap: var(--space-3); text-align: left; }
  form input { flex: 1; min-width: 0; }
</style>
