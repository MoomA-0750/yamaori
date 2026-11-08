// 設定（拡張機能の保存場所に置く）と、vrchat.com の応答から Yamaori へ送る形への変換。
export const DEFAULTS = {
  yamaoriUrl: 'http://localhost:47800',
  token: '',
  autoWorlds: false,   // 開いたページのワールドの情報を自動で送る（規約との関係の 3。既定は切る）
  autoFriends: false,  // フレンドの一覧を自動で送る（規約との関係の 3。既定は切る）
  bulkOpen: false,     // 情報のないワールドをまとめて開いて取る（規約との関係の 4。既定は切る）
  bulkIntervalSec: 10,
  bulkDailyLimit: 100,
}

export async function getSettings() {
  const s = await chrome.storage.local.get(DEFAULTS)
  return { ...DEFAULTS, ...s }
}

/** VRChat のワールドの応答 → Yamaori の WorldDetailsInput。 */
export function toWorldDetails(w) {
  const platforms = [...new Set((w.unityPackages ?? []).map((p) => p.platform).filter(Boolean))]
  return {
    id: w.id,
    body: {
      name: w.name ?? null,
      authorId: w.authorId ?? null,
      authorName: w.authorName ?? null,
      description: w.description ?? null,
      imageUrl: w.imageUrl ?? null,
      thumbnailUrl: w.thumbnailImageUrl ?? null,
      releaseStatus: w.releaseStatus ?? null,
      capacity: Number.isInteger(w.capacity) ? w.capacity : null,
      tags: Array.isArray(w.tags) ? w.tags : null,
      platforms: platforms.length ? platforms : null,
      visits: Number.isInteger(w.visits) ? w.visits : null,
      favorites: Number.isInteger(w.favorites) ? w.favorites : null,
    },
  }
}

export const isWorld = (x) => x && typeof x === 'object' && typeof x.id === 'string' && x.id.startsWith('wrld_')
