// ホーム画面に置けるようにするための Service Worker。手元に溜めることはせず、いつも Yamaori へ取りに行く（記録はいつも新しいものを見る）。
self.addEventListener('install', () => self.skipWaiting())
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()))
self.addEventListener('fetch', () => { /* 何もしない（ブラウザに任せる） */ })
