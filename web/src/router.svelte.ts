// URL の # のあとで画面を決める（#/days/2026-11-08 など）。サーバーの側で道を増やさずに済む。

export type Route =
  | { page: 'days' }
  | { page: 'day'; date: string }
  | { page: 'visit'; id: string }
  | { page: 'worlds' }
  | { page: 'world'; id: string }
  | { page: 'people' }
  | { page: 'person'; id: string }
  | { page: 'photos' }
  | { page: 'settings' }

function parse(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').map(decodeURIComponent)
  switch (parts[0]) {
    case 'day': return parts[1] ? { page: 'day', date: parts[1] } : { page: 'days' }
    case 'visit': return parts[1] ? { page: 'visit', id: parts[1] } : { page: 'days' }
    case 'worlds': return { page: 'worlds' }
    case 'world': return parts[1] ? { page: 'world', id: parts[1] } : { page: 'worlds' }
    case 'people': return { page: 'people' }
    case 'person': return parts[1] ? { page: 'person', id: parts[1] } : { page: 'people' }
    case 'photos': return { page: 'photos' }
    case 'settings': return { page: 'settings' }
    default: return { page: 'days' }
  }
}

export const router = $state({ route: parse(location.hash) as Route })

window.addEventListener('hashchange', () => {
  router.route = parse(location.hash)
  window.scrollTo({ top: 0 })
})

export const href = {
  days: () => '#/',
  day: (date: string) => `#/day/${date}`,
  visit: (id: string) => `#/visit/${encodeURIComponent(id)}`,
  worlds: () => '#/worlds',
  world: (id: string) => `#/world/${encodeURIComponent(id)}`,
  people: () => '#/people',
  person: (id: string) => `#/person/${encodeURIComponent(id)}`,
  photos: () => '#/photos',
  settings: () => '#/settings',
}
