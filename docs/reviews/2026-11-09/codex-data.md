読み取り専用で静的レビューしました。ファイル変更・ビルド・テスト実行はしていません。

## 重大

### 1. 転送先から写真が消えた後でも、過去の転送記録だけで手元を削除する

- ファイル: [PhotoSync.cs:48](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/PhotoSync.cs:48)、[PhotoCatalog.cs:187](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:187)
- 何が起きるか: `days` で写真 A を NAS に送り、その後 NAS 側で A が削除・破損・移動された場合でも、期限になると `photo_uploads` の古い記録だけを見てローカルの A を削除します。削除直前に `SizeAsync` を呼ばないため、両方から失われます。
- 直し方: ローカル削除の直前に、記録された `remote_path` を再照会し、現在も同じサイズで存在することを確認する。確認不能・不一致なら削除せず、転送記録を無効化して再送対象に戻す。可能ならサイズだけでなく SHA-256 も保存・照合する。

### 2. 同名写真が同じ転送先を上書きし、両方のローカルが削除され得る

- ファイル: [PhotoCatalog.cs:72](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:72)、[PhotoSync.cs:17](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/PhotoSync.cs:17)、[PhotoTargets.cs:35](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/PhotoTargets.cs:35)、[PhotoTargets.cs:140](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/PhotoTargets.cs:140)
- 何が起きるか:
  - 異なるフォルダに、同じ年月・同じファイル名で内容の異なる写真 A/B があると、転送先はどちらも `yyyy-MM/ファイル名` です。
  - サイズが異なる場合、B が A を上書きしますが、A/B 両方に転送済み記録が残るため、`after_upload` では両方のローカルが消え、転送先には B しか残りません。
  - ファイル名とサイズまで同じ場合、内容が違っても同じ写真 ID に潰れます。どちらか一方だけを転送した後、両方の置き場所が削除対象になります。
- 直し方: 写真 ID を内容のハッシュから作る。転送先名にもハッシュか衝突しない相対パスを含める。既存ファイルが同名でもハッシュが違う場合は上書きせず、別名にするか衝突として停止する。

### 3. VRCX から取り込んだメモを Yamaori で編集すると、次の取り込みで上書きされる

- ファイル: [VrcxImport.cs:195](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VrcxImport.cs:195)、[Queries.cs:323](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Queries.cs:323)
- 何が起きるか: VRCX の「ワールドメモ」を取り込み、Yamaori の通常のメモ編集 API で本文を書き直しても、次の `import-vrcx` が同じ IDへ無条件で VRCX の本文を書き戻します。API のメモ型に `source` が出ていないため、画面側も取り込みメモを区別できません。
- 直し方: 取り込み原本と本人の編集を別レコードにする。少なくとも `source=vrcx` のメモを直接編集不可にするか、編集時に新しい Yamaori メモへ複製する。再取り込みでは、前回取り込み時の本文ハッシュと一致する場合だけ更新する。

### 4. 別の VAT/VRCX DB を取り込むと、前に取り込んだ訪問が一括で消える

- ファイル: [VatImport.cs:19](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VatImport.cs:19)、[VatImport.cs:118](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VatImport.cs:118)、[VrcxImport.cs:21](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VrcxImport.cs:21)、[VrcxImport.cs:145](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VrcxImport.cs:145)、[ProjectionWriter.cs:20](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/ProjectionWriter.cs:20)
- 何が起きるか: 端末 A の VAT DB を取り込んだ後、端末 B の VAT DB を取り込むと、両方とも source が単なる `vat` なので、A の訪問・出会い・出来事を削除して B だけに置き換えます。VRCX も同様です。VRCX では古いメモだけ残る場合があり、複数の取り込み元が混ざった不整合にもなります。
- 直し方: source を `vat:<端末ID>:<DB識別子>`、`vrcx:<アカウント/DB識別子>` のように分ける。DB識別子は保存パスだけでなく、元DB内の一意情報または初回登録した import ID を使う。

## 高

### 5. 削除対象を列挙した後にパスが別写真へ置き換わると、新しい写真を削除する

- ファイル: [PhotoCatalog.cs:187](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:187)、[PhotoSync.cs:56](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/PhotoSync.cs:56)
- 何が起きるか: 写真 A を転送済みとして削除対象に列挙した後、削除までの間に同じパスへ写真 B が作られると、行63の `File.Delete(p.Path)` は B を削除します。削除時に現在のサイズ・mtime・写真 IDを再確認していません。
- 直し方: 削除直前にDBの location、現在のサイズ・mtime、できれば内容ハッシュを再照合する。不一致なら削除しない。安全性を上げるなら、検証済みファイルを同一ボリューム内の隔離場所へ原子的に rename してから削除する。

### 6. 同じ秒に作られたメモが、親へ一度も送られないことがある

- ファイル: [Database.cs:341](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Database.cs:341)、[SyncStore.cs:77](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/SyncStore.cs:77)、[SyncWorker.cs:58](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/SyncWorker.cs:58)
- 何が起きるか: 時刻は秒単位に丸められています。同期が `12:00:00Z` のメモ A を送信中にメモ B が同じ秒で作られると、送信後の透かしも `12:00:00Z` になります。次回は `updated_at > $since` なので、B は永久に対象外です。
- 直し方: `(updated_at, id)` の複合カーソルにするか、単調増加する変更番号を持たせる。親が受領確認した個々のメモ ID/版を記録する方式がより安全。

### 7. メモ削除は親へ同期されない。また時計ずれで新しい編集が捨てられる

- ファイル: [SyncStore.cs:104](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/SyncStore.cs:104)、[Queries.cs:329](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Queries.cs:329)
- 何が起きるか:
  - 子でメモを削除しても行自体がなくなり、同期対象にならないため親には残り続けます。
  - 親の時計が進んでいると、実時間では後に行った子の編集でも `updated_at` が小さくなり、`WHERE notes.updated_at < $u` により黙って捨てられます。
- 直し方: 削除墓石と版番号を同期する。端末時計による LWW ではなく、親が採番する revision、ETag、または競合を双方残す方式にする。

### 8. アカウント切り替え後の最後のアカウントしか `accounts` に現れず、VAT を誤ったアカウントへ付け得る

- ファイル: [LogProjector.cs:52](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/Log/LogProjector.cs:52)、[LogProjector.cs:141](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/Log/LogProjector.cs:141)、[Database.cs:152](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Database.cs:152)、[VatImport.cs:171](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VatImport.cs:171)
- 何が起きるか: 1本のログ内で main から alt へ切り替えると、訪問には両方の account ID が入りますが、session には最後の alt だけが保存されます。`accounts` view は session だけから作るため「アカウントは alt の1つ」と判定され、`import-vat` の全履歴を alt へ自動付与します。
- 直し方: accounts を `visits.account_id` とアカウント識別履歴から構成する。ログイン識別を session の単一列ではなく時系列の別表に残す。VAT の自動選択は、訪問に現れるアカウントも含めて1件のときだけにする。

### 9. データフォルダを複製した2台の子が、親で同じ端末として互いを上書きする

- ファイル: [Database.cs:302](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Database.cs:302)、[SyncStore.cs:90](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/SyncStore.cs:90)
- 何が起きるか: PC移行などで `yamaori.sqlite3` をコピーして旧PCと新PCを同時使用すると、両方が同じ device ID を持ちます。同じ名前の `output_log_*.txt` ができると source も同じになり、後から送った端末の projection が親で先の記録を置き換えます。
- 直し方: 親への初回登録時に端末鍵を発行する。複製検出用のインストール ID/鍵をDB外またはOS保護領域にも持ち、同じ device ID が異なる鍵から来たら受信を拒否して新IDへの分離を求める。

### 10. VAT と VRCX の同じ期間を取り込むと、同一訪問が二重になる

- ファイル: [VatImport.cs:32](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VatImport.cs:32)、[VrcxImport.cs:39](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VrcxImport.cs:39)
- 何が起きるか: 両取り込みともログの session との重複しか除外しません。ログが残っていない同じ期間を VAT と VRCX の両方から入れると、IDは `vat` と `vrcx` で別になるため、同じワールド・インスタンス・時刻の訪問が2件入ります。
- 直し方: 取り込み元間の優先順位を決めるか、`account/device + world + instance + 時刻許容差` の共通キーで照合する。自動統合が曖昧な場合は重複候補として本人に示す。

## 中

### 11. 電源断時、直前のメモなどが失われる耐久性設定になっている（要確認）

- ファイル: [Database.cs:22](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Database.cs:22)
- 何が起きるか: WAL の `synchronous=NORMAL` はプロセスクラッシュには強い一方、OS・電源断では直近のコミットを失う可能性があります。ログは原本から回復できますが、Yamaori だけに書いたメモや設定には回復元がありません。
- 直し方: 記録を失わないことを優先するなら `synchronous=FULL` を使う。少なくとも notes/settings の書き込み前後だけ FULL 相当にする設計を検討し、Windows実機で強制電源断試験を行う。

### 12. SQLite のロックが5秒を超えると、見張りサービスごと停止し得る

- ファイル: [Database.cs:22](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Database.cs:22)、[LogWatcher.cs:38](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/LogWatcher.cs:38)、[PhotoWatcher.cs:104](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/PhotoWatcher.cs:104)
- 何が起きるか: 常駐中に別プロセスで大きな VAT/VRCX 取り込みを行うなど、writer lock が5秒を超えると `SQLITE_BUSY` が出ます。両 watcher は `SqliteException` を捕捉しないため BackgroundService が異常終了し、既定設定ではホスト全体が停止します。その後、生ログの退避が止まります。
- 直し方: 書き込みをプロセス内の単一キューで直列化し、CLI取り込み時も常駐側へ依頼する。少なくとも busy/locked を指数バックオフで再試行し、BackgroundService の外へ例外を出さない。

### 13. 通知の重複排除が「所有元を1つだけ残す」ため、読み直しで通知が消える

- ファイル: [LogProjector.cs:102](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/Log/LogProjector.cs:102)、[ProjectionWriter.cs:53](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/ProjectionWriter.cs:53)
- 何が起きるか: 同じ通知がログA/Bにあり、Aが先に `events` を所有するとB側は `INSERT OR IGNORE` で捨てられます。後でAだけを新しいパーサーで読み直して、その通知を認識しなくなるとAの行が削除されます。Bの `log_events` には残っていても projection を再構築しない限り、表示上の通知は消えます。
- 直し方: 正規化した通知本体と、各 source からの provenance を別表にする。少なくとも置換時に、同じ通知 ID を持つ他sourceの raw event/import recordから再生成する。

### 14. `reparse` の削除と再投入が別トランザクション

- ファイル: [LogIngestor.cs:29](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/LogIngestor.cs:29)、[LogIngestor.cs:123](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/LogIngestor.cs:123)
- 何が起きるか: `Delete(name)` のコミット後、再読込前に停止・電源断すると、`log_files` と `log_events` は消えますが、古い visits/events は残ります。以後 `rebuild` ではそのログを見つけられず、DB内の原本と投影が食い違います。生ログが残っていれば再度 `reparse` して回復できます。
- 直し方: 新しい raw events と projection を一時表または同一トランザクションで構築し、成功時に一括交換する。

### 15. 圧縮後に元ログが伸びても、生ログの追記が永久に無視される（要確認）

- ファイル: [LogArchive.cs:15](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/LogArchive.cs:15)、[LogArchive.cs:41](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/LogArchive.cs:41)、[LogWatcher.cs:52](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/LogWatcher.cs:52)
- 何が起きるか: VRChat終了判定直後に遅延した最終書き込みが届くなど、`.gz` 作成後に元ファイルが伸びても、次回は `.gz` の存在だけで return します。DBは元ファイルから最終行を読めても、生ログの保存には入りません。
- 直し方: gzip に非圧縮時の長さ・末尾ハッシュを併記し、元が伸びていれば再圧縮する。圧縮前後で元ファイルの長さが安定していることも確認する。

### 16. タイムゾーンIDが見つからないと現在PCのゾーンへ黙って置換される

- ファイル: [LogIngestor.cs:140](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/LogIngestor.cs:140)、[LogProjector.cs:152](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/Log/LogProjector.cs:152)
- 何が起きるか: DBを別OS・別PCへ移し、保存済みの timezone ID が認識されない状態で rebuild/reparse すると、現在PCのローカルゾーンで全時刻を再解釈します。エラーにならず、数時間ずれた記録で上書きされます。
- 直し方: 見つからない場合は処理を停止し、明示的な timezone 指定を要求する。IANA/Windows IDの両方か、当時のUTCオフセット情報を保存する。
- 要確認: 夏時間終了時の重複するローカル時刻は `ConvertTimeToUtc` が片方のオフセットを選ぶため、同じ1時間内の順序や滞在時間がずれる可能性があります。

### 17. 同じ長さで中身だけ差し替わったログを検出できない

- ファイル: [LogIngestor.cs:42](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/LogIngestor.cs:42)、[LogArchive.cs:34](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/LogArchive.cs:34)
- 何が起きるか: 同名・同じバイト数の別ログへ置換された場合、DB側は「長さもclosedも同じ」として読みません。アーカイブも長さが同じなら差分をコピーしません。古い記録と古い生ログが残ります。
- 直し方: ファイルID、mtime、先頭・末尾ブロックのハッシュを状態に保存し、内容の置換を検出したら別sourceとして保全してから読み直す。

### 18. インストーラーが別データフォルダで動く Yamaori まで強制終了する

- ファイル: [yamaori.iss:61](/home/mooma-0750/Documents/git-cloned/yamaori/installer/yamaori.iss:61)、[yamaori.iss:82](/home/mooma-0750/Documents/git-cloned/yamaori/installer/yamaori.iss:82)
- 何が起きるか: 更新・アンインストール時の `taskkill /IM Yamaori.exe /F` と `Get-Process Yamaori | Stop-Process` は、実行ファイルの場所やデータフォルダを区別しません。開発版や別データフォルダのインスタンスも転送・DB処理の途中で止まります。
- 直し方: `{app}\Yamaori.exe` と一致するプロセスだけを対象にする。より安全には mutex/制御用パイプで対象インスタンスのPIDを特定し、そのPIDだけを終了する。

## 低・仕様確認

### 19. 日をまたぐ訪問の全滞在時間が、入室した日にだけ計上される（要確認）

- ファイル: [Queries.cs:122](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Queries.cs:122)
- 何が起きるか: 23:30〜翌01:00の訪問は、前日に90分、翌日に0分として集計されます。翌日のページにもその訪問は出ません。
- 直し方: 「入った日の記録」という仕様なら現状でよいです。「その日に過ごした時間」なら、ローカル日付の境界で滞在区間を分割して各日に配分する。

## 問題が見つからなかった観点

- 通常のログ追記では、raw event・読み位置・projection が同一トランザクションで更新され、再起動時の二重投入も主キーで防がれています。
- projection の訪問・出会いIDは端末・ログ名・バイト位置から安定して作られ、通常の rebuild ではメモの付け先が変わりません。
- VAT/VRCX の元DBは read-only 接続で開いており、取り込み元を書き換える経路はありません。
- 親への projection 適用は1トランザクションで、応答前のネットワーク断による再送も source 単位の置換なので通常は二重化しません。
- 写真転送は、転送直後の同サイズ確認が成功するまで `photo_uploads` を記録せず、既定の保持設定も `keep` です。
- フォルダ転送は一時ファイルから rename するため、通常のコピー途中のファイルを完成品として見せません。
- UTC保存、XMPのオフセット解釈、通常日のローカル日付境界は一貫しています。
- DB移行 v1〜v9 は版ごとにトランザクション化されており、途中のSQL失敗時はその版の `user_version` も進みません。
- インストール先とデータ先は分離され、管理者権限を要求せず、アンインストーラーにデータフォルダを削除する指定もありません。