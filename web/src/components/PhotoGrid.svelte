<script lang="ts">
  import { api, type PhotoView } from '../api'
  import { dateTime, time } from '../format'
  import { href } from '../router.svelte'
  import { settled } from '../dialog'

  import AlbumPicker from './AlbumPicker.svelte'
  import PhotoEditor from './PhotoEditor.svelte'

  // selecting のときは、押すと選ぶ（selected に ID が入る）。押さないときは大きく見る。
  // inTrash ならゴミ箱の写真（見るところで「戻す」）。onchange は、ゴミ箱に入れた・戻したあとに呼ぶ（一覧を読み直す）
  let { photos, showDate = false, selecting = false, selected = $bindable([]), inTrash = false, onchange }:
    { photos: PhotoView[]; showDate?: boolean; selecting?: boolean; selected?: string[]; inTrash?: boolean; onchange?: () => void } = $props()
  let picker = $state<string[] | null>(null)
  let editing = $state<string | null>(null)
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
  // 中身は、小窓が薄く消える動きが終わってから消す（消える間に中が空にならないように。
  // 次に別の写真を開いたとき、前の写真が残って見えないように、閉じ終えたら必ず消す）
  async function closed() {
    await settled(dialog)
    if (!dialog?.open) open = null
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

<dialog class="k-dialog viewer-dialog" bind:this={dialog} onclose={closed} onkeydown={keydown} aria-label="写真">
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
          <div>{dateTime(open.takenAt)}{#if open.edited}<span class="chip small-chip">直した情報</span>{/if}{#if open.worldName}<span class="sep">·</span>{#if open.worldId}<a href={href.world(open.worldId)} onclick={close}>{open.worldName}</a>{:else}{open.worldName}{/if}{/if}</div>
          <div class="faint">
            {#if open.authorName}撮った人 {open.authorName}<span class="sep">·</span>{/if}{#if open.width}{open.width}×{open.height}<span class="sep">·</span>{/if}{open.fileName}
          </div>
        </div>
        <div class="row">
          <button class="ghost" onclick={() => step(-1)} aria-label="前の写真" disabled={index <= 0}>←</button>
          <button class="ghost" onclick={() => step(1)} aria-label="次の写真" disabled={index >= photos.length - 1}>→</button>
          {#if open.available}<a class="chip" href={api.photoFile(open.id)} target="_blank" rel="noopener">もとの大きさ ↗</a>{/if}
          {#if !inTrash}<button onclick={() => (editing = open!.id)}>情報を直す</button><button onclick={() => (picker = [open!.id])}>アルバムに入れる</button>{/if}
          <button onclick={() => trashOne(open!)}>{inTrash ? '戻す' : 'ゴミ箱へ'}</button>
          <button onclick={close}>閉じる</button>
        </div>
      </div>
    </div>
  {/if}
</dialog>

{#if editing}<PhotoEditor photoId={editing} onclose={() => (editing = null)} onsaved={async () => { if (open) { const p = await api.photo(open.id).catch(() => null); if (p) { open = p; const i = photos.findIndex((x) => x.id === p.id); if (i >= 0) photos[i] = p } } }} />{/if}
{#if picker}<AlbumPicker photoIds={picker} onclose={() => (picker = null)} ondone={(m) => (note = m)} />{/if}

<style>
  .grid { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(9.5rem, 1fr)); gap: var(--space-2); }
  .cell { padding: 0; border: var(--border-width) solid var(--line); border-radius: var(--radius-sm); corner-shape: var(--corner-shape); overflow: hidden; width: 100%; aspect-ratio: 16 / 9; position: relative; background: var(--surface-2); display: block; min-height: 0; }
  .cell img { width: 100%; height: 100%; object-fit: cover; display: block; }
  .cell:hover { border-color: var(--accent); }
  /* 写真の上の字。明るい・暗いに関わらず、暗い幕に白い字 */
  .when { position: absolute; left: 0; right: 0; bottom: 0; padding: var(--space-4) var(--space-2) var(--space-1); font-size: var(--font-caption); color: var(--on-media); text-align: left;
    background: linear-gradient(transparent, var(--scrim)); font-variant-numeric: tabular-nums; }
  .picked { outline: calc(var(--focus-width) + var(--border-width)) solid var(--accent); outline-offset: calc((var(--focus-width) + var(--border-width)) * -1); }
  /* 選ぶときの丸。選んでいないときは幕の地に白い輪、選んだら主の色 */
  .check { position: absolute; top: var(--space-1); left: var(--space-1); width: var(--icon-lg); height: var(--icon-lg); border-radius: var(--radius-full);
    background: var(--scrim); border: calc(var(--border-width) * 2) solid var(--on-media); display: grid; place-items: center; font-size: var(--font-label); color: var(--on-accent); z-index: 1; }
  .picked .check { background: var(--accent); border-color: var(--accent); }
  .note { margin: 0; padding: var(--space-2) var(--space-4) 0; }
  .small-chip { font-size: var(--font-micro); padding: 0 var(--space-2); margin-left: var(--space-2); vertical-align: 0.1em; }
  .missing { display: grid; place-items: center; height: 100%; font-size: var(--font-note); }
  /* 大きく見る小窓。中身は写真なので、後ろの幕は --scrim。写真は縁まで（内側の余白なし） */
  .viewer-dialog { --nest-pad: 0px; width: min(1100px, calc(100vw - var(--space-8))); padding: 0; }
  .viewer-dialog::backdrop { background: var(--scrim); }
  .viewer { display: flex; flex-direction: column; }
  .frame { background: var(--yamaori-photo-mat); display: grid; place-items: center; min-height: 30vh; }
  .frame img { max-width: 100%; max-height: 76vh; display: block; }
  .frame p { color: var(--on-media); padding: var(--space-8); }
  .meta { padding: var(--space-3) var(--space-4); gap: var(--space-2) var(--space-4); }
  .sep { margin: 0 0.4em; }
  @media (max-width: 620px) { .grid { grid-template-columns: repeat(auto-fill, minmax(7rem, 1fr)); } }
</style>
