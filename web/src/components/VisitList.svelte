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
  .visits { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .5rem; }
  .visit { display: grid; grid-template-columns: 7.5rem 1fr; gap: .75rem; padding: .75rem 1rem; transition: border-color .15s ease; }
  .visit:hover { border-color: var(--accent); color: inherit; }
  .when { display: flex; flex-direction: column; font-variant-numeric: tabular-nums; }
  .date { font-size: .8rem; color: var(--ink-2); }
  .world { font-weight: 600; margin-bottom: .25rem; }
  .chips { gap: .3rem; }
  @media (max-width: 480px) {
    .visit { grid-template-columns: 1fr; gap: .25rem; }
    .when { flex-direction: row; gap: .5rem; align-items: baseline; flex-wrap: wrap; }
  }
</style>
