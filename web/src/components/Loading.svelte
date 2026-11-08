<script lang="ts">
  let { error = '' }: { error?: string } = $props()
</script>

{#if error}
  <p class="msg error" role="alert">{error}</p>
{:else}
  <p class="msg faint" aria-live="polite">読んでいます…</p>
{/if}

<style>
  .msg { padding: 2rem 0; text-align: center; }
  .error { color: var(--danger); }
</style>
