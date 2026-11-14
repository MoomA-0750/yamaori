<script lang="ts">
  import type { VisitSummary } from '../api'
  import { accessLabel, between, day, localDate, time, worldName } from '../format'
  import { href } from '../router.svelte'
  import { deviceLabel, loadDevices } from '../devices.svelte'

  let { visits, showDate = false, showWorld = true }: { visits: VisitSummary[]; showDate?: boolean; showWorld?: boolean } = $props()
  loadDevices()
</script>

<ol class="visits">
  {#each visits as v (v.id)}
    <li>
      <a class="visit card" href={href.visit(v.id)}>
        <div class="when">
          {#if showDate}<span class="date">{day(localDate(v.joinedAt))}</span>{/if}
          <span class="nowrap">{time(v.joinedAt)}–{v.leftAt ? time(v.leftAt) : 'いま'}</span>
          <span class="faint nowrap">{between(v.joinedAt, v.leftAt)}</span>
        </div>
        <div class="what">
          {#if showWorld}<div class="world break">{worldName(v)}</div>{/if}
          <div class="row chips">
            <span class="chip">{accessLabel(v.access)} · {v.region.toUpperCase()}</span>
            {#if !v.leftAt}<span class="chip live">いる</span>{/if}
            {#if v.peopleCount}<span class="chip">{v.peopleCount}人</span>{/if}
            {#if v.videoCount}<span class="chip">動画 {v.videoCount}</span>{/if}
            {#if v.screenshotCount}<span class="chip">写真 {v.screenshotCount}</span>{/if}
            {#if deviceLabel(v.deviceId)}<span class="chip" title="記録した端末">{deviceLabel(v.deviceId)}</span>{/if}
            {#if v.noteCount}<span class="chip accent">メモ {v.noteCount}</span>{/if}
          </div>
        </div>
      </a>
    </li>
  {/each}
</ol>

<style>
  .visits { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--space-2); }
  .visit { display: grid; grid-template-columns: 7.5rem 1fr; gap: var(--space-3); padding: var(--space-3) var(--space-4); transition: border-color var(--motion-fast) var(--ease-standard); }
  .visit:hover { border-color: color-mix(in srgb, var(--line-strong) 45%, transparent); background: color-mix(in srgb, var(--surface), var(--fg) 3%); color: inherit; }
  .when { display: flex; flex-direction: column; font-variant-numeric: tabular-nums; }
  .date { font-size: var(--font-note); color: var(--muted); }
  .world { font-weight: var(--weight-strong); margin-bottom: var(--space-1); }
  .chips { gap: var(--space-1); }
  @media (max-width: 480px) {
    .visit { grid-template-columns: 1fr; gap: var(--space-1); }
    .when { flex-direction: row; gap: var(--space-2); align-items: baseline; flex-wrap: wrap; }
  }
</style>
