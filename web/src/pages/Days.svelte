<script lang="ts">
  import { api, type DaySummary } from '../api'
  import { day, minutes } from '../format'
  import { href } from '../router.svelte'
  import { live } from '../live.svelte'
  import Loading from '../components/Loading.svelte'

  let days = $state<DaySummary[] | null>(null)
  let error = $state('')

  $effect(() => {
    live.version // 今の出来事が来たら読み直す
    api.days().then((d) => (days = d)).catch((e) => (error = (e as Error).message))
  })

  // 月ごとに分ける
  const months = $derived.by(() => {
    const m = new Map<string, DaySummary[]>()
    for (const d of days ?? []) {
      const k = d.date.slice(0, 7)
      m.set(k, [...(m.get(k) ?? []), d])
    }
    return [...m.entries()]
  })
</script>

<div class="stack">
  <h1>日記</h1>
  {#if !days}
    <Loading {error} />
  {:else if days.length === 0}
    <div class="card empty">
      <p>まだ記録がありません。</p>
      <p class="faint">VRChat で遊ぶと、行ったワールドと会った人がここに並びます。</p>
    </div>
  {:else}
    {#each months as [month, list] (month)}
      <section>
        <h2 class="month">{Number(month.slice(0, 4))}年{Number(month.slice(5))}月</h2>
        <ol class="days">
          {#each list as d (d.date)}
            <li>
              <a class="day card" href={href.day(d.date)}>
                <div class="date">{day(d.date)}</div>
                <div class="row nums">
                  {#if d.visitCount}
                    <span>{minutes(d.minutes)}</span>
                    <span class="faint">ワールド {d.worldCount}</span>
                    <span class="faint">訪問 {d.visitCount}</span>
                    <span class="faint">{d.peopleCount}人</span>
                  {/if}
                  {#if d.noteCount}<span class="chip accent">日記</span>{/if}
                </div>
              </a>
            </li>
          {/each}
        </ol>
      </section>
    {/each}
  {/if}
</div>

<style>
  .month { color: var(--ink-2); font-size: .9rem; margin: .25rem 0 .5rem; }
  .days { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .5rem; }
  .day { display: flex; justify-content: space-between; align-items: center; gap: .5rem 1rem; flex-wrap: wrap; padding: .8rem 1rem; }
  .day:hover { border-color: var(--accent); color: inherit; }
  .date { font-weight: 600; }
  .nums { gap: .35rem .9rem; font-variant-numeric: tabular-nums; }
  .empty { padding: 1.5rem; text-align: center; }
  .empty p { margin: .25rem 0; }
</style>
