// 記録を集めた端末の名前。端末が 2 つ以上のときだけ、訪問に「どの端末か」を出す。
import { api, type DeviceView } from './api'

export const devices = $state({ list: [] as DeviceView[] })

let loading: Promise<void> | null = null

/** 一度だけ読む（ログインの前に失敗したら、次に呼ばれたとき読み直す）。 */
export function loadDevices() {
  loading ??= api.devices().then((d) => { devices.list = d }).catch(() => { loading = null })
  return loading
}

/** 端末の名前。端末が 1 つだけなら出す意味がないので null。 */
export function deviceLabel(id: string | null | undefined): string | null {
  if (!id || devices.list.length < 2) return null
  return devices.list.find((d) => d.id === id)?.name ?? null
}
