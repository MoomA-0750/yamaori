読み取り専用の静的レビューを行いました。HEAD は指定どおり `221372f`、作業ツリーは変更なしです。ビルド・テストは生成物を書き込むため実行していません。

結論の全体像は、確定 3 件、変更 16 件、本人の承認が必要 1 件です。特に D7・D8・D11・D17 は、公開後に直すと既存 ID や同期データの移行が増えるため、今変える方を選びます。

## D1. トークンの範囲

- 結論: **確定（今のまま）**
- 理由: `full` でも管理口は通らず、`extension` と `sync` は明示的な経路だけに制限されています。sync トークンの最初の端末への結び付きも実装済みです。旧トークンを一律無効化すると既存クライアントを理由なく止めるため、既定 `full` の移行にも覆すほどの理由はありません。`src/Yamaori/Web/Auth.cs:83`、`src/Yamaori/Web/Auth.cs:86`、`src/Yamaori/Web/Auth.cs:90`、`src/Yamaori/Web/Api.cs:112`、`src/Yamaori.Store/Database.cs:324`
- 最小の形: 変更なし。旧トークンが `full` であることを設定画面でも警告できればなおよいですが、設計条件ではありません。
- 戻せるか: 各トークンを失効・再発行できるので戻せます。

## D2. 人を忘れる

- 結論: **確定（今のまま）**
- 理由: 既存の encounter・event・friend snapshot・person note を同一トランザクションで消し、以後の再投入もトリガーで拒否します。名前だけの場合も `user_id IS NULL` に限定され、同名の ID 既知の別人を巻き込みません。`src/Yamaori.Store/ForgetStore.cs:11`、`src/Yamaori.Store/ForgetStore.cs:17`、`src/Yamaori.Store/ForgetStore.cs:26`、`src/Yamaori.Store/Database.cs:336`
- 最小の形: 本体の意味は変更なし。端末間の反映だけは D19 で補います。
- 戻せるか: 拒否設定は解除でき、ログ由来の記録は再構築できます。削除したメモは戻りません。この非対称性は現在の説明どおりで妥当です。

## D3. SFTP の相手の鍵

- 結論: **変える — 初回は画面で指紋を確認して「信じる」を押す**
- 理由: TOFU 自体より、写真設定を保存しただけで全 SFTP 鍵を削除し、次回の相手を無条件に覚え直す点が弱いです。送り先・保持日数など無関係な変更でもピン留めが外れます。`src/Yamaori/Photos/PhotoTargets.cs:102`、`src/Yamaori/Photos/PhotoTargets.cs:108`、`src/Yamaori/Photos/PhotoTargets.cs:123`、`src/Yamaori/Web/Auth.cs:411`
- 最小の形: 初回接続は鍵を拒否しつつ指紋だけ取得し、設定画面に表示。本人の確認後に保存して再接続する。「鍵を忘れる」は送り先ごとの専用操作にし、通常の設定保存では消さない。
- 戻せるか: TOFU に戻せます。保存済み指紋の形式も現在の `SHA256:…` を維持できます。

## D4. 日の区切り

- 結論: **変える — 記録した端末でのローカル日を正とする**
- 理由: 現状は親の `_tz` で UTC の入室時刻を再変換するため、旅先で体験した日が親の居場所の日へ移ります。ログのタイムゾーンは既に保存されていますが、訪問や sync payload には渡りません。`src/Yamaori.Store/Database.cs:76`、`src/Yamaori.Store/Database.cs:82`、`src/Yamaori.Store/Queries.cs:122`、`src/Yamaori.Store/Queries.cs:132`、`src/Yamaori.Store/SyncStore.cs:49`
- 最小の形: visit に `local_date` と `time_zone` または当時の UTC offset を保存し、sync・VAT・VRCX にも含める。日のメモの ID もこのローカル日を使う。
- 戻せるか: UTC 時刻と元タイムゾーンを残せば、親基準への再集計も可能です。

## D5. 日をまたぐ訪問

- 結論: **変える — 各日に重なった時間を配分し、訪問は両方の日に表示する**
- 理由: 現在は 23:30〜01:00 の全 90 分を入室日に加え、翌日には表示もしません。「過ごした時間」の記録としては日別合計が誤解を招きます。`src/Yamaori.Store/Queries.cs:122`、`src/Yamaori.Store/Queries.cs:137`、`src/Yamaori.Store/Queries.cs:148`
- 最小の形: D4 のタイムゾーンを使って日境界で区間を分割する。`visit_days` のような投影表を持てば、D13 の SQL 集計にも使えます。
- 戻せるか: 元の visit は分割せず UTC 区間を保つため、表示・集計規則だけ戻せます。

## D6. 子にだけ入るもの

- 結論: **変える — 写真本体以外の永続データは親へ送る**
- 理由: 親が WebUI と DB の正本なのに、現状は `log_files` の source とメモしか送りません。子で取り込んだ履歴や保存したワールドが親から見えないのは、親子構成の意味と衝突します。`src/Yamaori.Store/SyncStore.cs:22`、`src/Yamaori.Store/SyncStore.cs:31`、`src/Yamaori/SyncWorker.cs:52`、`README.md:105`
- 最小の形:
  - VAT/VRCX は `vat:<device>/<import-id>`、`vrcx:<device>/<import-id>` として送る。
  - ワールド詳細・保存一覧・フレンド記録は device/source 付きで送り、親で決定的に統合する。
  - 写真のバイト列は今までどおり NAS 経由とし、同期対象にしない。
- 戻せるか: 同期機能を止めることはできます。親へ入った子由来データを消せるよう、source を必ず保持する必要があります。

## D7. メモの削除と時計ずれ

- 結論: **変える — 削除墓石と親採番の revision を入れる**
- 理由: 削除は行が消えて同期対象にならず、編集競合は端末時計の `updated_at` 比較だけなので、正しい新しい編集も捨てられます。`src/Yamaori.Store/Queries.cs:330`、`src/Yamaori.Store/SyncStore.cs:77`、`src/Yamaori.Store/SyncStore.cs:123`、`src/Yamaori.Store/SyncStore.cs:134`
- 最小の形: note に `deleted_at` と revision を持たせる。子は前回親から得た revision を base として操作を送り、親が新 revision を返す。不一致なら黙って捨てず、競合コピーを残す。
- 戻せるか: スキーマ移行前の DB バックアップから戻せます。墓石を物理削除へ戻すことも可能です。

## D8. データフォルダを複製した子

- 結論: **変える — DB 外の端末鍵で複製を検出する**
- 理由: device ID は DB 内だけにあり、コピーすると source と安定 ID まで一致します。親は同一端末からの更新と区別できません。`src/Yamaori.Store/Database.cs:351`、`src/Yamaori.Store/SyncStore.cs:105`、`src/Yamaori.Store/SyncStore.cs:118`
- 最小の形: OS 保護領域に端末鍵を作り、親が sync token を device ID と公開鍵の組へ結び付ける。別鍵から同じ ID が来たら拒否し、「移行」か「複製」を選ばせる。複製なら新しい self device ID を発行し、過去の記録は旧 ID のまま残す。
- 戻せるか: 親で結び付きを解除できます。既存データの ID を一括変更しない形なら安全に戻せます。

## D9. 設定が 2 か所

- 結論: **確定（今のまま）**
- 理由: 起動前に必要な listen・port・親・写真設定を JSON、稼働中に即時反映する認証・セッション・公開範囲を DB に置く境界は合理的です。全部を DB にすると起動前設定を読むために DB 依存が増え、全部を JSON にすると認証情報の同時更新が弱くなります。`src/Yamaori/Program.cs:27`、`src/Yamaori/Web/Auth.cs:280`、`src/Yamaori/Web/Auth.cs:287`、`src/Yamaori.Store/AuthStore.cs:26`
- 最小の形: 変更なし。各キーの正本を一覧化し、同じ意味の値を両方に置かない。
- 戻せるか: 将来、統一された設定ストアの façade の背後へ隠すことは可能です。

## D10. 「保存」の固定日本語

- 結論: **変える — 内部キーと表示名を分ける**
- 理由: API の既定値と DB の list name が `"保存"` に固定され、言語変更や名称変更がデータ上の別リストになります。`src/Yamaori/Web/Api.cs:52`、`src/Yamaori/Web/Api.cs:54`、`src/Yamaori.Store/Queries.cs:343`、`src/Yamaori.Store/Queries.cs:351`
- 最小の形: 組み込みキー `saved` とローカライズ表示名「保存」を分ける。既存の `source='yamaori', list_name='保存'` を `saved` に移す。利用者が作ったリスト名はそのまま。
- 戻せるか: 旧 API の `"保存"` を alias として残せるので戻せます。

## D11. 写真の同一性

- 結論: **変える — 内容の SHA-256 を写真 ID と転送先名に使う**
- 理由: 同名・同サイズ・別内容が一つの ID に潰れます。また送り先が同サイズの別内容へ替わっても削除条件を通ります。mtime 確認はローカル置換の一部しか防ぎません。`src/Yamaori.Store/PhotoCatalog.cs:13`、`src/Yamaori.Store/PhotoCatalog.cs:72`、`src/Yamaori/Photos/PhotoSync.cs:47`、`src/Yamaori/Photos/PhotoSync.cs:86`、`src/Yamaori/Photos/PhotoSync.cs:99`
- 最小の形: 初回索引時に SHA-256 を計算して保存し、ID とリモート名に短いハッシュを含める。削除直前にローカルハッシュを再確認する。既存 upload 記録は安全側に倒し、再確認できるまで削除権限として扱わない。
- 戻せるか: 移行前バックアップからは戻せます。リモートに作ったハッシュ付きファイル名は自動では元へ戻らないため、移行は一方向として扱うべきです。

## D12. ログインの全体ロック

- 結論: **変える — 全員を 5 分止める固定ロックを廃止する**
- 理由: 攻撃者が 31 回失敗するだけで正しい遠隔利用者を繰り返し締め出せます。PBKDF2 の同時数は既に 2 に制限されているので、CPU 防御は全体ロックなしでも構成できます。`src/Yamaori/Web/Auth.cs:157`、`src/Yamaori/Web/Auth.cs:161`、`src/Yamaori/Web/Auth.cs:170`、`src/Yamaori/Web/Auth.cs:202`
- 最小の形: 検証前の全体トークンバケット＋接続元別の指数バックオフ。接続元は D14 で検証済みの転送情報だけを使う。
- 戻せるか: 制限値・方式は設定変更で戻せます。

## D13. sync 量と日の一覧

- 結論: **変える — 構造上限とページングを入れる**
- 理由: projection には配列数・各文字列長の検証がなく、30 MB 内でも大量要素や長いトランザクションを作れます。日の一覧は全 visit と encounter を毎回メモリへ読みます。`src/Yamaori/Web/Api.cs:112`、`src/Yamaori/Web/Api.cs:122`、`src/Yamaori.Store/Queries.cs:123`、`src/Yamaori.Store/Queries.cs:125`
- 最小の形:
  - sync は visit・encounter・event 数、ID・名前・data 長を制限する。
  - 大きい source は batch ID 付きで分割受信し、最後に一括交換する。
  - days は D5 の日別投影を SQL 集計し、期間または cursor でページングする。
- 戻せるか: 上限と既定ページ数は変更できます。API は旧形式を互換経路として残せます。

## D14. Tailscale のヘッダー

- 結論: **変える — loopback だけでは信頼しない**
- 理由: 現在は任意の loopback プロキシから来た `Tailscale-User-Login` を本人認証として扱い、管理口にも届きます。`src/Yamaori/Web/Auth.cs:119`、`src/Yamaori/Web/Auth.cs:127`、`src/Yamaori/Web/Auth.cs:129`、`src/Yamaori.Core/Access.cs:55`
- 最小の形: forwarded source IP を Tailscale LocalAPI の WhoIs 等で検証し、結果のログイン名とヘッダーが一致した場合だけ使う。検証不能なら Tailscale 認証扱いにせず、Yamaori のログインを要求する。
- 戻せるか: 信頼方式を差し替えられます。パスワード認証が残るため利用不能にはなりません。

## D15. ストア・CI・CHANGELOG

- 結論: **変える — CI と CHANGELOG は置く。拡張機能は当面ストアに出さず、署名済みリリース物で配る**
- 理由: CI と変更履歴は公開・DB 移行・複数プラットフォームの最低限の検証経路です。一方、拡張機能は任意の HTTP/HTTPS host permission と規約上グレーな自動取得を含み、現状の一体 manifest のままストアへ出す判断は害が大きいです。`docs/plan.md:155`、`extension/manifest.json:4`、`extension/manifest.json:16`、`extension/manifest.json:27`
- 最小の形: CI は .NET test、Web build、拡張機能テスト、インストーラー構築。CHANGELOG は DB migration と互換性を必須記載。配布用拡張機能では自動取得権限を別 build/明示 opt-in に分離する。
- 戻せるか: CI・CHANGELOG は削除可能です。ストア公開は後から選べます。

## D16. GitHub 公開

- 結論: **本人にしか決められない**
- 理由: 設計上の推奨は **公開** です。README の MIT 公開方針、MIT LICENSE、winget 配布と整合します。origin も既に GitHub を指し、ローカル main は `origin/main` より 65 commit 進んでいます。ただし公開は履歴・秘密・他者情報を一度開示すると完全には取り消せず、実際の push と公開範囲変更は本人の承認が必要です。`README.md:70`、`README.md:72`、`LICENSE:1`、`.git/config:6`
- 最小の形: 公開前に履歴を含む秘密・実 DB・ログ・写真・実 ID の検査を行い、D11・D14・D17・D18 を少なくとも公開版の既知制約から外す。今回 push はしていません。
- 戻せるか: 非公開→公開は可能ですが、公開→非公開で既存 clone・fork・キャッシュは回収できません。

## D17. reparse・通知 provenance・同サイズログ置換

- 結論: **変える — 3 件とも直す**
- 理由:
  - reparse は先に raw 状態を別トランザクションで消します。`src/Yamaori.Store/LogIngestor.cs:29`、`src/Yamaori.Store/LogIngestor.cs:32`、`src/Yamaori.Store/LogIngestor.cs:123`
  - 通知は本体と所有 source が一体で、別ログ側は `INSERT OR IGNORE` で失われます。`src/Yamaori.Store/ProjectionWriter.cs:53`
  - 変更検出は長さだけです。`src/Yamaori.Store/LogIngestor.cs:42`、`src/Yamaori.Store/LogIngestor.cs:43`
- 最小の形:
  - reparse は新 raw events を一時領域で作り、raw・projection を一トランザクションで交換。
  - 通知本体と `event_sources` provenance を分離。
  - log state にファイル識別子、mtime、先頭・末尾ハッシュを保存し、置換時は旧生ログを別名保全。
- 戻せるか: 元ログと旧 DB バックアップがあれば戻せます。新 provenance 表を持つ方が旧形式への再投影も容易です。

## D18. 閉じたあと伸びたログの保存

- 結論: **変える**
- 理由: `.gz` が存在すると即 return するため、終了判定後の遅延追記を永久に保存しません。DB は元ログを読めても、再構築用の原本が欠けます。`src/Yamaori.Store/LogArchive.cs:15`、`src/Yamaori.Store/LogArchive.cs:23`、`src/Yamaori/LogWatcher.cs:60`
- 最小の形: 終了後、長さが複数回安定してから圧縮する。gzip に元長と末尾ハッシュを併記し、元が伸びたら再作成する。内容が置換された場合は旧 gzip を消さず別名保存する。
- 戻せるか: 圧縮タイミングの規則なので戻せます。

## D19. 「忘れた人」の端末間での意味

- 結論: **変える — 忘れる一覧を親が管理する全端末共通のプライバシー規則にする**
- 理由: 現在 `forgotten_people` はローカル DB にしかなく、子で忘れても親の既存記録は消えません。親で解除しても、子の projection hash が変わらないため記録が再送されない場合があります。`src/Yamaori.Store/ForgetStore.cs:24`、`src/Yamaori.Store/ForgetStore.cs:51`、`src/Yamaori.Store/SyncStore.cs:65`、`src/Yamaori/SyncWorker.cs:52`
- 最小の形: 追加はどの端末からでも親へ送る。親が revision を振って全子へ配り、各端末で削除・拒否する。解除は親だけが行い、該当 source の再送・再投影を明示的に要求する。
- 戻せるか: ローカルだけの規則へ戻せますが、削除済みメモは復元できません。

## D20. 組み立て直せないデータの自動バックアップ

- 結論: **変える — 親 DB の世代バックアップを既定で自動化する**
- 理由: 現在は migration 前バックアップと手動 `backup` だけです。メモ・トークン・拡張機能データ・写真転送記録はログから戻りません。本人一人向けでは、手動操作を忘れた一度の DB 障害が全損になります。`src/Yamaori.Store/Database.cs:41`、`src/Yamaori.Store/Database.cs:53`、`README.md:66`、`README.md:67`
- 最小の形: 親で日次の `VACUUM INTO` を行い、直近の有限世代を保持する。保存先と保持数を設定可能にし、既定は DataDir 内。外部ディスクへのバックアップは利用者の選択とする。
- 戻せるか: 自動実行を無効化し、作成済みバックアップを削除できます。

以上から、実装順の依存関係は `D4+D5+D13`、`D6+D7+D8+D19`、`D11`、`D17+D18` の4まとまりです。これは作業時間ではなく、それぞれ同じスキーマ・同期規約・移行処理を一緒に変えるべき部品のまとまりです。