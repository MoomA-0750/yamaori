<script lang="ts">
  import { api, type DayView } from '../api'
  import { day, minutes } from '../format'
  import { href } from '../router.svelte'
  import { live } from '../live.svelte'
  import VisitList from '../components/VisitList.svelte'
  import Notes from '../components/Notes.svelte'
  import Loading from '../components/Loading.svelte'

  let { date }: { date: string } = $props()
  let data = $state<DayView | null>(null)
  let error = $state('')

  $effect(() => {
    live.version
    api.day(date).then((d) => (data = d)).catch((e) => (error = (e as Error).message))
  })

  const total = $derived(
    (data?.visits ?? []).reduce((s, v) => s + (v.leftAt ? (new Date(v.leftAt).getTime() - new Date(v.joinedAt).getTime()) / 60000 : 0), 0),
  )

  function shift(n: number) {
    const [y, m, d] = date.split('-').map(Number)
    const t = new Date(y, m - 1, d + n)
    return `${t.getFullYear()}-${String(t.getMonth() + 1).padStart(2, '0')}-${String(t.getDate()).padStart(2, '0')}`
  }
</script>

<div class="stack">
  <nav class="spread">
    <a href={href.day(shift(-1))} class="faint">← 前の日</a>
    <a href={href.days()} class="faint">日記の一覧</a>
    <a href={href.day(shift(1))} class="faint">次の日 →</a>
  </nav>
  <header>
    <h1>{day(date)}</h1>
    {#if data && data.visits.length}
      <p class="muted">{minutes(total)} · ワールド {new Set(data.visits.map((v) => v.worldId)).size} · 訪問 {data.visits.length}</p>
    {/if}
  </header>

  {#if !data}
    <Loading {error} />
  {:else}
    {#if data.visits.length}
      <VisitList visits={data.visits} />
    {:else}
      <p class="faint">この日の訪問はありません。</p>
    {/if}
    {#key date}
      <Notes targetType="day" targetId={date} notes={data.notes} title="この日の日記" placeholder="この日のことを書く（Markdown が使える）" />
    {/key}
  {/if}
</div>
