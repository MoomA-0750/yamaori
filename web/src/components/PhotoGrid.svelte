<script lang="ts">
  import { api, type PhotoView } from '../api'
  import { dateTime, time } from '../format'
  import { href } from '../router.svelte'

  let { photos, showDate = false }: { photos: PhotoView[]; showDate?: boolean } = $props()
  let open = $state<PhotoView | null>(null)
  let dialog = $state<HTMLDialogElement>()

  function show(p: PhotoView) {
    open = p
    dialog?.showModal()
  }
  function close() {
    dialog?.close()
  }
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
      <button class="cell" onclick={() => show(p)} aria-label={`写真 ${dateTime(p.takenAt)}`}>
        {#if p.available}
          <img src={api.thumb(p.id, 320)} alt="" loading="lazy" decoding="async" />
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
      <div class="meta spread">
        <div>
          <div>{dateTime(open.takenAt)}{#if open.worldName}<span class="sep">·</span>{#if open.worldId}<a href={href.world(open.worldId)} onclick={close}>{open.worldName}</a>{:else}{open.worldName}{/if}{/if}</div>
          <div class="faint">
            {#if open.authorName}撮った人 {open.authorName}<span class="sep">·</span>{/if}{#if open.width}{open.width}×{open.height}<span class="sep">·</span>{/if}{open.fileName}
          </div>
        </div>
        <div class="row">
          <button class="ghost" onclick={() => step(-1)} aria-label="前の写真">←</button>
          <button class="ghost" onclick={() => step(1)} aria-label="次の写真">→</button>
          {#if open.available}<a class="chip" href={api.photoFile(open.id)} target="_blank" rel="noopener">もとの大きさ ↗</a>{/if}
          <button onclick={close}>閉じる</button>
        </div>
      </div>
    </div>
  {/if}
</dialog>

<style>
  .grid { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(9.5rem, 1fr)); gap: .5rem; }
  .cell { padding: 0; border: 1px solid var(--line); border-radius: var(--radius-sm); overflow: hidden; width: 100%; aspect-ratio: 16 / 9; position: relative; background: var(--surface-2); display: block; min-height: 0; }
  .cell img { width: 100%; height: 100%; object-fit: cover; display: block; }
  .cell:hover { border-color: var(--accent); }
  .when { position: absolute; left: .35rem; bottom: .3rem; font-size: .72rem; color: #fff; text-shadow: 0 1px 3px rgb(0 0 0 / 70%); font-variant-numeric: tabular-nums; }
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
