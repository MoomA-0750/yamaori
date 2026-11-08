読み取り専用の静的レビューです。ファイル変更・ビルド・テスト実行はしていません。

## 高

### 1. 同名・同サイズ・別内容の写真は、今も1枚に潰れて両方の手元が消える

- ファイル: `src/Yamaori.Store/PhotoCatalog.cs:72`、`src/Yamaori/Photos/PhotoSync.cs:34-54,74-94`
- 何が起きるか: フォルダA/Bに同じファイル名・同じバイト数だが内容の違う写真を置くと、`StableId.Of("photo", name, size)` が同じIDになります。転送はどちらか1つだけを送り、`photo_uploads` は共通のIDに付きます。その後 `after_upload` ではA/B両方の置き場所が削除対象になり、送り先には片方しか残りません。今回追加された別名は「サイズが違う衝突」にしか使われません。
- 直し方: 写真IDを内容のSHA-256等から作り、転送先名にもハッシュを入れる。既存DBは場所ごとのハッシュを段階的に計算して分離する。

### 2. 削除直前の確認がサイズだけなので、同サイズの別写真を消せる

- ファイル: `src/Yamaori/Photos/PhotoSync.cs:79-93`
- 何が起きるか: 写真Aを転送後、削除対象の列挙前後に同じパスを同じサイズの写真Bへ置き換えると、`local.Length == p.Size` を通りBを削除します。送り先のAが同サイズの壊れた内容や別内容に置換された場合も、Aの手元を削除します。
- 直し方: 目録にmtimeと内容ハッシュを保持し、削除直前にパス・サイズ・mtime・ハッシュを再照合する。送り先も可能ならハッシュ、最低でもETag等で確認する。

### 3. 訪問が0件の取り込みDBは、別DBでも見分けを素通りする

- ファイル: `src/Yamaori.Store/ImportGuard.cs:10-18`、`src/Yamaori.Store/VrcxImport.cs:151-156,185-199`
- 何が起きるか: `CheckOrigin` は既存の `visits` がある場合しか別DBを拒否しません。例えば訪問表が空でお気に入りだけあるVRCX DB Aを取り込み、続けて別のDB Bを取り込むと、originが違っても検査を通過します。`DELETE FROM world_favorites WHERE source='vrcx'` によりAのお気に入りが消え、出来事だけのDBでも `ProjectionWriter.Replace` で前の出来事が消えます。
- 直し方: `visits` だけでなく、events・VRCXメモ・お気に入り・保存済みoriginの存在で判定する。本筋は取り込み元ごとにsource/import IDを分け、別DBを同居させる。

### 4. Tailscale本人ヘッダーの信頼元は、まだ識別されていない

- ファイル: `src/Yamaori/Web/Auth.cs:113-130`、`src/Yamaori.Core/Access.cs:55-60`
- 何が起きるか: `listen=tailscale` で、loopbackのリバースプロキシが外部入力の `Tailscale-User-Login: 許可済みユーザー` をそのまま転送すると、正規のtailscale serveか確認せず本人として通します。設定・トークン・終了を含む管理口にも届きます。
- 直し方: Tailscale専用待受または信頼済みプロキシを明示し、その接続元だけでヘッダーを読む。任意のloopbackプロキシを信頼しない。

### 5. VAT/VRCXの重複回避が、別ワールドの正規の訪問まで捨てる

- ファイル: `src/Yamaori.Store/ImportGuard.cs:28-35`、`src/Yamaori.Store/VatImport.cs:61-69`、`src/Yamaori.Store/VrcxImport.cs:54-75`
- 何が起きるか: VRCXに10:00–12:00のワールドAがある状態で、VATに11:00のワールドBがあると、時刻がAの区間内というだけでBを飛ばします。ワールド、インスタンス、アカウントを比較していません。
- 直し方: `account + world + instance + 入室時刻の許容差` で重複候補を判定する。単なる時間区間の包含では除外しない。

## 中

### 6. ログのない子端末では、最初のメモ同期が成功扱いで失われる

- ファイル: `src/Yamaori/Web/Api.cs:112-126`、`src/Yamaori/SyncWorker.cs:52-68`
- 何が起きるか: 未結合のsyncトークンで、子にメモはあるが `LocalSources` が空の場合、projectionが送られないためトークンの`device_id`が未設定です。`POST /api/sync/notes` は `onlyDevice=""` として全メモを無視しつつ200を返し、子は透かしを進めます。そのメモは以後送られません。
- 直し方: notesにも明示的なdevice IDを持つ包みを使い、未結合ならそのIDへ原子的に結合する。未結合のままなら403を返し、子が送信済みにしないようにする。

### 7. `name:Alice` を忘れると、IDが判明している別のAliceまで将来記録されない

- ファイル: `src/Yamaori.Store/Database.cs:336-341`、`src/Yamaori.Store/ForgetStore.cs:32-36`
- 何が起きるか: 初回削除は `user_id IS NULL AND name='Alice'` だけですが、トリガーはuser IDの有無を見ず `name:Alice` に一致する全挿入を削除します。後日 `usr_x` という別人のAliceに会っても、そのencounter/eventが消えます。
- 直し方: 名前キーとの照合には `NEW.user_id IS NULL` を加える。ID既知の人はIDだけで拒否する。

### 8. 忘れた人のメモは、子端末から再出現する

- ファイル: `src/Yamaori.Store/Database.cs:329-344`、`src/Yamaori.Store/SyncStore.cs:126-138`
- 何が起きるか: v11にはencounters・events・friend_snapshotsのトリガーしかありません。親で人を忘れた後、子に残るその人向けメモを同期すると、`notes`へ再挿入されます。
- 直し方: `notes`にも `target_type='person' AND target_id IN forgotten_people` のINSERT/UPDATE拒否を加える。同期側でも拒否件数を返す。

### 9. 入力上限の修正は一部だけで、sync projectionと全件集計が残る

- ファイル: `src/Yamaori/Web/Api.cs:112-128`、`src/Yamaori.Store/Queries.cs:123-145`
- 何が起きるか: fullトークンから巨大な`SyncProjection`を繰り返し送れます。配列件数・各文字列長の上限がありません。また`GET /api/days`は全訪問・全encounterを毎回メモリへ読みます。DB肥大化とCPU・メモリ枯渇が残ります。
- 直し方: projectionの本文サイズ、配列数、ID・名前・dataの長さを検査する。daysはSQL集計またはページングへ移す。

### 10. 31回の失敗で、正しい遠隔利用者を繰り返し締め出せる

- ファイル: `src/Yamaori/Web/Auth.cs:151-172,188-212`
- 何が起きるか: 攻撃者が10分以内に31回誤ったログインを送ると、LAN/Tailscaleの全利用者が5分間429になります。PBKDF2同時実行数は塞がりましたが、遠隔利用者全体の固定ロックは残っています。
- 直し方: 接続元別制限を主にし、全体側は小さい同時実行数とトークンバケットで処理量を抑える。全員を一定時間拒否する状態は作らない。

### 11. ログイン必須設定では、インストーラーの終了APIが通らず強制終了になる

- ファイル: `installer/yamaori.iss:81-93`、`src/Yamaori/Web/Auth.cs:80-82,286-291`
- 何が起きるか: `local_requires_login=true` の利用者を更新すると、インストーラーの認証なし `POST /api/settings/shutdown` は401です。20秒待った後、対象exeを`Stop-Process -Force`するため、DB・NAS処理を区切って止める目的が失われます。
- 直し方: インストール単位の制御用秘密またはnamed pipeで対象PIDへ終了を依頼する。失敗時の強制終了対象を実行パスで限定している点は正しいです。

### 12. `synchronous=FULL` とセッション更新の組み合わせで、全API要求が耐久書き込みになる

- ファイル: `src/Yamaori.Store/Database.cs:22-24`、`src/Yamaori.Store/AuthStore.cs:90-96`
- 何が起きるか: Cookie付きのGETを含む各要求が`last_seen_at`を更新し、FULL設定でコミットします。画面の一度の表示でも複数回fsyncし、遅いディスクやNAS上のDBでは待ち・`SQLITE_BUSY`を増やします。
- 直し方: `last_seen_at`は例えば1時間に一度だけ更新する。重要データの書き込みとアクセス時刻の書き込みを分ける。

### 13. 種類だけ変える写真設定は、古い送り先を新しい形式として保存できる

- ファイル: `src/Yamaori/Web/Auth.cs:376-400`
- 何が起きるか: 現在が `folder`・`C:\Photos` のとき、`PUT /api/settings/photos` に `{"kind":"webdav"}` だけ送ると、targetを検査せず保存します。再起動後は`C:\Photos`をWebDAV URIとして生成しようとして、写真送信が毎回例外になります。
- 直し方: `input.Kind`と`input.Target`を現在値と合成した最終設定全体を検証してから保存する。

### 14. 旧トークンは移行後すべて`full`のまま

- ファイル: `src/Yamaori.Store/Database.cs:324-327`
- 何が起きるか: 更新前に拡張機能へ渡していたトークンも`scope='full'`になります。管理口は塞がりますが、記録全体の読み書き・削除は可能です。
- 直し方: 更新後に旧トークンの用途確認と再発行を促す。安全側へ寄せるなら旧トークンを一旦無効化する。

## 前回から未修正のデータ問題

以下は対象差分で実質的な変更がありません。

- `SyncStore.cs:126-138`、`Queries.cs:330-333` — メモ削除の墓石がなく、子の削除は親へ届かない。端末時計によるLWWも残る。墓石と単調revisionが必要。
- `Database.cs:348-363`、`SyncStore.cs:105-119` — DBを複製した2台は同じdevice IDのまま互いを上書きできる。DB外の端末鍵との照合が必要。
- `LogProjector.cs:102-114`、`ProjectionWriter.cs:53-60` — 通知の所有元が1つだけで、所有元の再構築により通知が消える問題が残る。通知本体とprovenanceを分離する。
- `LogIngestor.cs:29-36,123-131` — reparseの削除と再投入は別トランザクションのまま。途中停止でraw状態だけ消える。
- `LogArchive.cs:23` — `.gz`作成後に元ログが伸びても無視する問題が残る。
- `LogIngestor.cs:42-50`、`LogArchive.cs:29-42` — 同名・同じ長さで内容だけ差し替わったログは検出できない。
- `Queries.cs:122-145` — 日またぎ訪問を入室日に全計上する仕様は変更なし。仕様として選ぶなら問題なし。

## 塞がったと確認できた指摘

- セキュリティ1: 大文字小文字を変えたAPI/OpenAPI/MCP迂回は、`UseRouting`後のendpoint判定と大小無視の経路判定で塞がっています。
- セキュリティ2: 設定・パスワード・トークン・終了は全Bearerトークンから403になり、新規トークンには範囲があります。ただし旧トークンは上記14。
- セキュリティ3: 保存済み接続秘密は、保存済みの種類・接続先と一致するときだけ補完されます。
- セキュリティ5: 動画リンクはhttp/httpsだけリンク化されます。
- セキュリティ6: 直接接続ではOriginのポートも比較され、`localhost:3000`から`localhost:47800`へのPOSTは拒否されます。
- セキュリティ7: PWA共有は明示的な「保存する」操作が必要になりました。
- セキュリティ8: port・間隔・URL・ルート写真フォルダの主要な異常値は拒否されます。ただし上記13。
- セキュリティ12: 外部画像URLはHTTPSのvrchat.com/vrchat.cloud系だけに制限されました。
- データ3: VRCXメモを編集すると`source=NULL`になり、再取り込みで上書きされません。
- データ6: 同秒メモは時刻と送信済みIDの組で再取得されます。
- データ8: VATの自動アカウント判定は訪問上のaccount IDも含めます。
- データ11: SQLiteは`FULL`になり、電源断耐久性は上がっています。性能面は上記12。
- データ12: watcherはDBロックを含む例外でサービスごと停止せず、次回再試行します。
- データ16: 不明なタイムゾーンは現在PCのゾーンへ黙って置換せず停止します。
- データ18: 強制終了対象はインストール先と同じexeだけに限定されました。
- DB v11移行: v10→v11はトランザクション内で版更新され、移行前バックアップと未来版DBの拒否もあります。問題は移行手順ではなく、上記7・8のトリガー内容です。