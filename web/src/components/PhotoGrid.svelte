<script lang="ts">
  import { api, type PhotoView } from '../api'
  import { dateTime, time } from '../format'
  import { href } from '../router.svelte'

  import AlbumPicker from './AlbumPicker.svelte'

  // selecting のときは、押すと選ぶ（selected に ID が入る）。押さないときは大きく見る。
  // inTrash ならゴミ箱の写真（見るところで「戻す」）。onchange は、ゴミ箱に入れた・戻したあとに呼ぶ（一覧を読み直す）
  let { photos, showDate = false, selecting = false, selected = $bindable([]), inTrash = false, onchange }:
    { photos: PhotoView[]; showDate?: boolean; selecting?: boolean; selected?: string[]; inTrash?: boolean; onchange?: () => void } = $props()
  let picker = $state<string[] | null>(null)
  let note = $state('')

  function toggle(id: string) {
    selected = selected.includes(id) ? selected.filter((x) => x !== id) : [...selected, id]
  }
  async function trashOne(p: PhotoView) {
    if (inTrash) await api.restorePhotos([p.id]); else await api.trashPhotos([p.id])
    note = inTrash ? '戻しました' : 'ゴミ箱に入れました'
    const i = photos.findIndex((x) => x.id === p.id)
    const next = photos[i + 1] ?? photos[i - 1]
    if (next && next.id !== p.id) open = next; else close()
    onchange?.()
  }
  let open = $state<PhotoView | null>(null)
  let dialog = $state<HTMLDialogElement>()

  function show(p: PhotoView) {
    if (selecting) { toggle(p.id); return }
    note = ''
    open = p
    dialog?.showModal()
  }
  function close() {
    dialog?.close()
  }
  const index = $derived(open ? photos.findIndex((p) => p.id === open!.id) : -1)
  function step(n: number) {
    if (!open) return
    const i = photos.findIndex((p) => p.id === open!.id)
    const next = photos[i + n]
    if (next) open = next
  }
  function keydown(e: KeyboardEvent) {
    if (e.key === 'ArrowRight') step(1)
    if (e.key === 'ArrowLeft') step(-1)
  }
</script>

<ul class="grid">
  {#each photos as p (p.id)}
    <li>
      <button class="cell" class:picked={selecting && selected.includes(p.id)} onclick={() => show(p)} aria-pressed={selecting ? selected.includes(p.id) : undefined} aria-label={`写真 ${dateTime(p.takenAt)}`}>
        {#if selecting}<span class="check" aria-hidden="true">{selected.includes(p.id) ? '✓' : ''}</span>{/if}
        {#if p.available}
          <img src={api.thumb(p.id, 320)} alt="" loading="lazy" decoding="async" onerror={(e) => ((e.currentTarget as HTMLImageElement).style.visibility = 'hidden')} />
        {:else}
          <span class="missing faint">見つからない</span>
        {/if}
        <span class="when">{showDate ? dateTime(p.takenAt) : time(p.takenAt)}</span>
      </button>
    </li>
  {/each}
</ul>

<dialog bind:this={dialog} onclose={() => (open = null)} onkeydown={keydown} aria-label="写真">
  {#if open}
    <div class="viewer">
      <div class="frame">
        {#if open.available}
          <img src={api.thumb(open.id, 1600)} alt={`${open.worldName ?? ''} ${dateTime(open.takenAt)}`} />
        {:else}
          <p class="faint">このファイルは今は見つかりません（移したか消した）。</p>
        {/if}
      </div>
      {#if note}<p class="note faint" role="status">{note}</p>{/if}
      <div class="meta spread">
        <div>
          <div>{dateTime(open.takenAt)}{#if open.worldName}<span class="sep">·</span>{#if open.worldId}<a href={href.world(open.worldId)} onclick={close}>{open.worldName}</a>{:else}{open.worldName}{/if}{/if}</div>
          <div class="faint">
            {#if open.authorName}撮った人 {open.authorName}<span class="sep">·</span>{/if}{#if open.width}{open.width}×{open.height}<span class="sep">·</span>{/if}{open.fileName}
          </div>
        </div>
        <div class="row">
          <button class="ghost" onclick={() => step(-1)} aria-label="前の写真" disabled={index <= 0}>←</button>
          <button class="ghost" onclick={() => step(1)} aria-label="次の写真" disabled={index >= photos.length - 1}>→</button>
          {#if open.available}<a class="chip" href={api.photoFile(open.id)} target="_blank" rel="noopener">もとの大きさ ↗</a>{/if}
          {#if !inTrash}<button onclick={() => (picker = [open!.id])}>アルバムに入れる</button>{/if}
          <button onclick={() => trashOne(open!)}>{inTrash ? '戻す' : 'ゴミ箱へ'}</button>
          <button onclick={close}>閉じる</button>
        </div>
      </div>
    </div>
  {/if}
</dialog>

{#if picker}<AlbumPicker photoIds={picker} onclose={() => (picker = null)} ondone={(m) => (note = m)} />{/if}

<style>
  .grid { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(9.5rem, 1fr)); gap: .5rem; }
  .cell { padding: 0; border: 1px solid var(--line); border-radius: var(--radius-sm); overflow: hidden; width: 100%; aspect-ratio: 16 / 9; position: relative; background: var(--surface-2); display: block; min-height: 0; }
  .cell img { width: 100%; height: 100%; object-fit: cover; display: block; }
  .cell:hover { border-color: var(--accent); }
  .when { position: absolute; left: 0; right: 0; bottom: 0; padding: 1rem .4rem .25rem; font-size: .75rem; color: #fff; text-align: left;
    background: linear-gradient(transparent, rgb(0 0 0 / 65%)); font-variant-numeric: tabular-nums; }
  .picked { outline: 3px solid var(--accent); outline-offset: -3px; }
  .check { position: absolute; top: .3rem; left: .3rem; width: 1.4rem; height: 1.4rem; border-radius: 50%; background: rgb(255 255 255 / 90%);
    border: 2px solid rgb(0 0 0 / 45%); display: grid; place-items: center; font-size: .85rem; color: var(--accent); z-index: 1; }
  .picked .check { background: var(--accent); color: #fff; border-color: var(--accent); }
  .note { margin: 0; padding: .4rem 1rem 0; }
  .missing { display: grid; place-items: center; height: 100%; font-size: .8rem; }
  dialog { border: 0; padding: 0; background: var(--surface); color: var(--ink); border-radius: var(--radius); width: min(96vw, 1100px); max-height: 94vh; box-shadow: 0 10px 40px rgb(0 0 0 / 40%); }
  dialog::backdrop { background: rgb(0 0 0 / 70%); }
  .viewer { display: flex; flex-direction: column; }
  .frame { background: #000; display: grid; place-items: center; min-height: 30vh; }
  .frame img { max-width: 100%; max-height: 76vh; display: block; }
  .frame p { color: #ccc; padding: 2rem; }
  .meta { padding: .7rem 1rem; gap: .5rem 1rem; }
  .sep { margin: 0 .4em; }
  @media (max-width: 480px) { .grid { grid-template-columns: repeat(auto-fill, minmax(7rem, 1fr)); } }
</style>
