<script lang="ts">
  import { api, type Note, type TargetType } from '../api'
  import { render } from '../markdown'
  import { dateTime } from '../format'

  let { targetType, targetId, notes = $bindable([]), title = 'メモ', placeholder = 'このときのことを書く（Markdown が使える）' }:
    { targetType: TargetType; targetId: string; notes?: Note[]; title?: string; placeholder?: string } = $props()

  let draft = $state('')
  let editing = $state<string | null>(null)
  let editText = $state('')
  let busy = $state(false)
  let error = $state('')

  async function add() {
    if (!draft.trim()) return
    busy = true; error = ''
    try {
      notes = [...notes, await api.addNote(targetType, targetId, draft)]
      draft = ''
    } catch (e) { error = `書けなかった: ${(e as Error).message}` }
    busy = false
  }

  function edit(n: Note) { editing = n.id; editText = n.body }

  async function save(n: Note) {
    busy = true; error = ''
    try {
      const u = await api.updateNote(n.id, editText)
      notes = notes.map((x) => (x.id === n.id ? u : x))
      editing = null
    } catch (e) { error = `直せなかった: ${(e as Error).message}` }
    busy = false
  }

  let confirming = $state<string | null>(null)
  async function remove(n: Note) {
    busy = true; error = ''
    try {
      await api.deleteNote(n.id)
      notes = notes.filter((x) => x.id !== n.id)
      confirming = null
    } catch (e) { error = `消せなかった: ${(e as Error).message}` }
    busy = false
  }

  function keydown(e: KeyboardEvent, f: () => void) {
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); f() }
  }
</script>

<section class="notes">
  <h2 class="section-title">{title} {#if notes.length}<span class="faint">{notes.length}</span>{/if}</h2>

  {#each notes as n (n.id)}
    <article class="note card">
      {#if editing === n.id}
        <textarea rows="6" bind:value={editText} onkeydown={(e) => keydown(e, () => save(n))} aria-label="メモを直す"></textarea>
        <div class="row end">
          <button class="ghost" onclick={() => (editing = null)} disabled={busy}>やめる</button>
          <button class="primary" onclick={() => save(n)} disabled={busy}>保存</button>
        </div>
      {:else}
        <div class="prose">{@html render(n.body)}</div>
        <div class="spread meta">
          <span class="faint">{dateTime(n.updatedAt)}{n.updatedAt !== n.createdAt ? '（直した）' : ''}</span>
          <span class="row">
            {#if confirming === n.id}
              <span class="faint">消す？</span>
              <button class="ghost" onclick={() => (confirming = null)}>やめる</button>
              <button class="danger" onclick={() => remove(n)} disabled={busy}>消す</button>
            {:else}
              <button class="ghost" onclick={() => edit(n)}>直す</button>
              <button class="ghost danger" onclick={() => (confirming = n.id)}>消す</button>
            {/if}
          </span>
        </div>
      {/if}
    </article>
  {/each}

  <div class="compose">
    <textarea rows="3" bind:value={draft} {placeholder} onkeydown={(e) => keydown(e, add)} aria-label="新しいメモ"></textarea>
    <div class="row end">
      <span class="faint hint">Ctrl+Enter で保存</span>
      <button class="primary" onclick={add} disabled={busy || !draft.trim()}>書き足す</button>
    </div>
  </div>
  {#if error}<p class="error" role="alert">{error}</p>{/if}
</section>

<style>
  .notes { display: flex; flex-direction: column; gap: .75rem; }
  .note { padding: .9rem 1rem .5rem; }
  .meta { margin-top: .4rem; }
  .meta button { min-height: 2rem; padding: .2rem .6rem; font-size: .85rem; }
  .compose { display: flex; flex-direction: column; gap: .5rem; }
  .end { justify-content: flex-end; }
  .hint { margin-right: auto; }
  .error { color: var(--danger); margin: 0; }
  @media (max-width: 480px) { .hint { display: none; } }
</style>
