読み取り専用でレビューしました。ファイル変更・ビルド・試験実行はしていません。作業ツリーは変更なしです。

確認できた高リスクの欠陥が1件、中が5件、低が3件あります。

## 指摘

### 1. 写真の照合後から削除までに書き換わると、未転送の内容を削除する

- 重さ: 高（未確認）
- 場所: [PhotoSync.cs:94](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/PhotoSync.cs:94)、[PhotoSync.cs:107](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/PhotoSync.cs:107)、[PhotoSync.cs:117](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/PhotoSync.cs:117)
- 具体的な入力と結果:
  - 送り先と手元のSHA-256を照合した直後、`File.Delete` より前に、VRChatや同期ソフトが手元のファイルを同名・同サイズの新しい内容へ置き換える。
  - ハッシュ時のファイルは転送済みでも、実際に削除されるのは置換後の未転送ファイルです。
  - `HashOf` が `FileShare.ReadWrite | FileShare.Delete` で開いており、照合と削除の間に排他性がありません。
- 未確認: VRChatが実運用で写真をこのタイミングに再度書き換えるかは確認していません。ただし他の同期・補正ソフトでも成立します。
- 最小の直し方: 削除対象を、同一ボリューム上のYamaori専用一時名へ原子的に移してから、そのファイルと送り先を照合する。違えば元へ戻し、同じ場合だけ一時ファイルを削除する。少なくともWindowsでは、照合から削除まで書き込み共有を許さないハンドルを保持する。
- 試験の穴: [PhotoSyncTests.cs:177](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/PhotoSyncTests.cs:177) は同期開始前に送り先を置換するだけで、照合と削除の間の競合を作っていません。

### 2. 取り込みの切り方が時間だけで、別ワールドの訪問を切っている

- 重さ: 中
- 場所: [ImportGuard.cs:46](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/ImportGuard.cs:46)、[VrcxImport.cs:61](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VrcxImport.cs:61)、[VatImport.cs:55](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VatImport.cs:55)、[VatImport.cs:66](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VatImport.cs:66)
- 具体的な入力と結果:
  - VRCXに World A の訪問 `12:00–12:30` がある。
  - Yamaoriのログセッションが `12:10–12:20` だが、ログ側の訪問は World B・別インスタンス・別アカウントである。
  - `ClipToCovered` はワールド・インスタンス・アカウントを見ず、World Aを12:10で切ります。
  - VATでは12:10以降のWorld Aの会った人や動画まで無条件に飛ばします。
- 最小の直し方: `sessions` の時間範囲ではなくログ由来の `visits` と照合し、区間が重なり、かつワールド・インスタンス・アカウントが一致するときだけ切る。
- 試験の穴: [VrcxImportTests.cs:154](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/VrcxImportTests.cs:154) はセッションしか作らず、ログ側のワールド・インスタンス・アカウントを用意していないため、確定事項に反する時間だけの判定を正しいものとして通しています。

### 3. 消したメモが、古い子からの再送で復活する

- 重さ: 中
- 場所: [SyncStore.cs:170](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/SyncStore.cs:170)、[SyncStore.cs:189](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/SyncStore.cs:189)
- 具体的な入力と結果:
  - 新しい子がメモを削除し、親が `/sync/notes/deletions` を受け取って物理削除する。
  - 親は削除墓石を保存しません。
  - その後、古い版の子、復元した子DB、または複製された子が、同じIDと古い`updated_at`のメモを `/sync/notes` へ再送する。
  - 親には競合行がないため、`INSERT` で削除済みメモが復活します。
- 最小の直し方: 親にも削除墓石を保存する。`ApplyNotes` は、同じID・端末の墓石があり、メモの`updated_at <= deleted_at`なら再投入を拒否する。親で削除後に明示的に直した新しい版だけを許す。
- 試験の穴: [SyncTests.cs:92](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/SyncTests.cs:92) と [HttpSyncTests.cs:15](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/HttpSyncTests.cs:15) は削除直後だけを確認し、古いメモの再送を試していません。

### 4. v11ですでに忘れていた人は、v12移行後も受け手・持ち主として残る

- 重さ: 中
- 場所: [Database.cs:357](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Database.cs:357)
- 具体的な入力と結果:
  - v11 DBの`forgotten_people`に`usr_bob`がある。
  - 同じDBに、`receiver_id=usr_bob`の通知と、`owner_id=usr_bob`の訪問が残っている。v11の`Forget`ではこの2種類を消していなかったため、実在し得る状態です。
  - v12移行は`AFTER INSERT`トリガーを作るだけなので、既存行は掃除されません。
  - 更新後も忘れた人のIDと通知本文がAPIから読めます。
- 最小の直し方: migration 12内で、既存`forgotten_people`と一致する受け手通知を削除し、既存訪問の`owner_id`と`instance`も更新してからトリガーを作る。
- 試験の穴: [StoreTests.cs:123](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/StoreTests.cs:123) はv9から移行後に改めて`Forget`を呼んでいます。v11時点ですでに忘れていた状態からの11→14を試していません。

### 5. 毎日のバックアップが途中で壊れると、その日は再試行しない

- 重さ: 中
- 場所: [BackupWorker.cs:33](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/BackupWorker.cs:33)、[Database.cs:62](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Database.cs:62)
- 具体的な入力と結果:
  - `VACUUM INTO auto-20261109.sqlite3`の途中で電源断、容量不足、プロセス強制終了が起きる。
  - 不完全なファイルが残る。
  - 次回は`File.Exists(file)`だけで成功済みと判断し、その日は再試行しません。
  - 最新の正常なバックアップが前日以前になり、利用者は壊れた写しを正常だと思う可能性があります。
- 最小の直し方: 同じフォルダの一時ファイルへ`VACUUM INTO`し、`PRAGMA integrity_check`で検証後、完成名へ原子的に置き換える。既存の日次ファイルも検証し、壊れていれば再作成する。
- 試験の穴: [BackupTests.cs:10](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/BackupTests.cs:10) は正常に完成したファイルだけを扱い、空・途中・壊れた同日ファイルを置いた場合を試していません。

### 6. JSONとして正しいが型が壊れた設定では、依然として起動できない

- 重さ: 中
- 場所: [ConfigFile.cs:43](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/ConfigFile.cs:43)、[Program.cs:34](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Program.cs:34)
- 具体的な入力と結果:
  - `yamaori.json`を`{"Port":"abc"}`や`{"PhotoUpload":"broken"}`にする。
  - `Recover`はJSONとして読めるため退避しません。
  - その後の`config.Bind(options)`が型変換で失敗し、Yamaoriは起動しません。
  - CHANGELOGの「yamaori.jsonが壊れたときも起きる」は、構文破損だけに限られています。
- 最小の直し方: 設定のbindと値検証までを回復判定に含める。変換できなければ同じ退避処理を行い、既定値で一度だけ組み立て直す。
- 試験の穴: [ConfigFileTests.cs:28](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/ConfigFileTests.cs:28) は途中で切れたJSONしか試していません。

### 7. 24時間以上続く現在の訪問は、日・ワールドとも24時間で止まる

- 重さ: 低
- 場所: [Queries.cs:139](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Queries.cs:139)、[Queries.cs:160](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Queries.cs:160)
- 具体的な入力と結果:
  - `left_at=NULL`のまま30時間継続している訪問を表示する。
  - 実際は1800分でも、日とワールドの両方が1440分になります。
  - 確定事項の「今いる訪問は現在時刻まで数える」と一致しません。
- 最小の直し方: 実際に現在も開いているセッションの訪問は現在時刻まで数える。閉じ忘れ対策は、セッションが閉じているのに`left_at`だけNULLの行に限定する。
- 試験の穴: [QueryTests.cs:39](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/QueryTests.cs:39) は90分しか確認していません。

### 8. 旧APIの「保存」互換は、前後に空白があると別リストになる

- 重さ: 低
- 場所: [Api.cs:224](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Api.cs:224)
- 具体的な入力と結果:
  - APIへ`"list":" 保存 "`を送る。
  - alias判定を`Trim`より先に行うため、結果は`saved`ではなく日本語の`保存`になります。
  - 画面上はどちらも「保存」と見える一方、DBには二つの内部リストができます。
- 最小の直し方: 最初に`list = list?.Trim()`し、その後で空文字・`保存`・`saved`を`saved`へ正規化する。
- 試験の穴: [HttpScopeTests.cs:142](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/HttpScopeTests.cs:142) は完全一致の`"保存"`だけです。

### 9. Windows固有部分がCIで一度も動かない

- 重さ: 低
- 場所: [.github/workflows/ci.yml:11](/home/mooma-0750/Documents/git-cloned/yamaori/.github/workflows/ci.yml:11)
- 具体的な結果:
  - CIは`ubuntu-latest`のみです。
  - DPAPI、Windowsタイムゾーン名、上書き`File.Move`、名前付きMutex、WinExe、インストーラー固有の破損は、mainへ入ってもCIが検出しません。
  - 今回追加された「秘密を読めない場合」も、Linuxでは`dpapi:`を復号処理せずnullにする経路しか通りません。
- 最小の直し方: `ubuntu-latest`と`windows-latest`のmatrixで.NET試験を行う。Windows側ではDPAPIの正常往復・別ユーザー相当の復号失敗・設定の上書きを追加する。インストーラー構築は別jobでもよいです。

## 総評

トークン範囲、検査後の端末結び付け、reparseの一トランザクション化、伸びたgzip、MCP上限、本人限定の忘れるAPIは、確定事項どおりに直っています。

一方、取り込みの切り方は確定事項の「ワールド＋インスタンス＋アカウント」照合を実装しておらず、メモ墓石も親側では永続化されていません。DB 11→14移行では、v11ですでに忘れていた人のデータが残る点が主要な移行漏れです。