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
    <div class="card empty stack">
      <div>
        <p>まだ記録がありません。</p>
        <p class="faint">VRChat で遊ぶと、行ったワールドと会った人がここに並びます。Yamaori は VRChat のログを読むだけで、VRChat にはつなぎません。</p>
      </div>
      <div class="start">
        <h2>はじめに</h2>
        <ol>
          <li><b>VRChat を起こす。</b>ログが書かれるそばから記録に入ります（この画面は自動で変わります）。</li>
          <li><b>前の記録を入れる（あれば）。</b>VRChat は 1 日より古いログを消すので、それより前の分は VRChatActivityTools や VRCX の記録から取り込めます:
            <code>yamaori import-vat --file &lt;VRChatActivityLog.db&gt;</code>・<code>yamaori import-vrcx</code></li>
          <li><b>写真を見せる。</b>VRChat の写真のフォルダは最初から見ています。NAS に移した写真は <a href={href.settings()}>設定</a> で足します。</li>
          <li><b>ワールドの詳しい情報（任意）。</b>ブラウザの拡張機能を入れると、vrchat.com で開いたワールドの説明や人数の上限を保存できます。</li>
          <li><b>スマホや別の PC から見る（任意）。</b><a href={href.settings()}>設定</a> でパスワードを決めてから、見せ方を変えます。</li>
        </ol>
      </div>
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
  .start { text-align: left; max-width: 40rem; margin: 0 auto; }
  .start h2 { font-size: 1rem; margin-bottom: .3rem; }
  .start ol { margin: 0; padding-left: 1.3rem; display: flex; flex-direction: column; gap: .4rem; }
  .start code { font-family: var(--mono); font-size: .82em; background: var(--surface-2); padding: .05em .35em; border-radius: 5px; overflow-wrap: anywhere; }
  .start a { color: var(--accent); text-decoration: underline; }
</style>
