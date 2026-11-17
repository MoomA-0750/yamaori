// 小窓（<dialog class="k-dialog">）を閉じたあと、薄く消える動きが終わるのを待つ。
// 中身を消す・部品を外すのはそのあとにする（消える間に中が空になったり、1コマで消えたりしないように）。
// 動きを減らす設定などで動きが無ければ、すぐ終わる。
export async function settled(el: Element | undefined): Promise<void> {
  if (!el) return
  await Promise.all(el.getAnimations({ subtree: true }).map((a) => a.finished.catch(() => {})))
}
