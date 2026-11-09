## 指摘

1. **高 — ゴミ箱を空にすると、PhotoSync の「送り先」まで削除する**

   場所: [PhotoAlbums.cs:154](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoAlbums.cs:154)、[PhotoSync.cs:95](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/PhotoSync.cs:95)

   `PhotoSync` の送り先が `folder` の場合、送ったファイルを `photo_locations` に追加します。`Empty` は `photo_locations` の全パスを無条件に削除するため、UIの「写真の送り先に送った写しは消しません」という説明に反して、SMB/NAS上の送り先も削除します。

   具体例: PCの写真を `folder:/mnt/backup` へ送り、`after_upload` でPC側を削除する。その後ゴミ箱に入れて空にすると、唯一残っている `/mnt/backup/yyyy-MM/photo.png` も削除され、写真を完全に失います。WebDAV/SFTPだけは `photo_locations` に入らないため残ります。

   最小の直し方: `Empty` に監視対象の `EffectivePhotoDirs` を渡し、その配下だけを削除するか、`photo_locations` に `source/watch/upload-target` の種別を持たせ、送り先の場所を除外する。

   試験不足: [AlbumTests.cs:49](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/AlbumTests.cs:49) は「NASとして直接スキャンした場所」しか扱っておらず、`FolderTarget` が登録した場所を保護する試験がありません。

2. **高 — 写真IDの衝突やパスの差し替えで、ゴミ箱に入れていない別画像を消せる**

   場所: [PhotoCatalog.cs:76](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:76)、[PhotoAlbums.cs:158](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoAlbums.cs:158)

   XMPのない写真IDはファイル名とバイト数だけで決まります。別のフォルダに「同じ名前・同じ大きさ・異なる内容」の画像があると同じ写真として束ねられ、片方をゴミ箱に入れて空にすると両方消えます。

   また、目録作成後に同じパスのファイルが別画像へ差し替えられ、次のスキャン前にゴミ箱を空にすると、新しいファイルも確認なしで削除されます。`PhotoSync` の削除処理にはサイズ・mtime・内容の再確認がありますが、`Empty` にはありません。

   最小の直し方: 削除直前にサイズ・mtimeを目録と照合し、可能なら内容ハッシュも確認する。IDもファイル名＋サイズではなく、初回走査時の内容ハッシュまたは永続的なランダムIDを使う。

   試験不足: 同名・同サイズ・異内容の複数ルート、スキャン後のパス差し替え、削除中の並行更新が未試験です。

3. **高 — XMP書き込みで、Yamaoriが知らない既存メタデータを不可逆に消す**

   場所: [PhotoXmp.cs:66](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/PhotoXmp.cs:66)、[PhotoXmp.cs:70](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Core/PhotoXmp.cs:70)

   既存の `XML:com.adobe.xmp` チャンクを丸ごと捨て、Yamaoriが生成した限定的なXMPへ置き換えています。

   具体例: PNGに `dc:rights`、タイトル、編集履歴、撮影機材、GPS、レーティングなどが入っている状態で「写真のファイルにも書く」を実行すると、それらは消えます。既存XMPが先頭1MBより後ろにあり、読み取り時には認識できなかった場合も、書き込み時には削除されます。

   最小の直し方: 既存XMPをXMLとして読み、Yamaoriが管理する要素と変更対象のVRChat要素だけを更新し、未知の名前空間・要素を保存する。安全にマージできないXMPは書き換えを拒否する。

   試験不足: [PhotoEditTests.cs:20](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/PhotoEditTests.cs:20) はYamaori生成XMPの往復だけで、未知要素の保存を確かめていません。

4. **中 — XMP書き込み後の状態がPhotoSyncへ反映されず、直したファイルが送り直されない**

   場所: [PhotoEdits.cs:147](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoEdits.cs:147)、[PhotoEdits.cs:151](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoEdits.cs:151)、[PhotoCatalog.cs:316](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:316)

   書き込み後に更新するのは `photo_locations.size/mtime` だけで、`photos.size` と既存の `photo_uploads` は古いままです。`PendingUploads` は送信記録の存在だけで除外するため、WebDAV/SFTP側にはXMPなしの古いファイルが残り、再送されません。

   `after_upload` の削除はローカル実サイズと古い `photos.size` が違うため通常は止まり、直ちに原本を失う経路にはなりません。ただし同期は恒久的に停滞し、送り先をバックアップとして使っている場合、ローカルをゴミ箱で消すと、送り先には直した情報のない版しか残りません。

   最小の直し方: 全場所への書き込み成功後に `photos.size` を代表値へ更新し、対象写真の `photo_uploads` を削除して再送対象に戻す。複数場所でサイズが異なる可能性があるなら、同期判定は `photo_locations.size` と内容ハッシュを使う。

   試験不足: XMP書き込み前後で `PendingUploads`、WebDAV/SFTP再送、`after_upload` を組み合わせた試験がありません。

5. **中 — ゴミ箱を空にしても `.yamaori/thumbs` の画像が残る**

   場所: [PhotoAlbums.cs:173](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoAlbums.cs:173)、[Thumbnails.cs:25](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Thumbnails.cs:25)

   `Empty` が消す縮小はDataDirの `thumbsDir` だけです。`ThumbSidecar` やRemoteThumbsが各写真ルートの `.yamaori/thumbs` に作ったJPEGは残ります。

   具体例: 個人情報を含む写真をゴミ箱から完全削除しても、NAS上には最大1600pxの閲覧可能な画像が残り続けます。

   最小の直し方: 削除前に各 `photo_locations.root` を集め、既知の全サイズについて `Thumbnails.SidecarPath` も削除する。失敗した場合は写真本体と同様に結果へ報告する。

   試験不足: Sidecarを作った後のゴミ箱消去が未試験です。

6. **中 — 手で直した「一緒にいた人」が、時刻からの推定を上書きせず加算される**

   場所: [PhotoCatalog.cs:200](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:200)、[PhotoCatalog.cs:213](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:213)、[PhotoCatalog.cs:122](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:122)

   `ByPerson` と `List(personId:)` は、遭遇時間による推定と `photo_edits.people` をOR/加算しています。

   具体例: 時刻推定でCarolが含まれた写真を編集し、「一緒にいた人」をDaveだけに直しても、Carolの人別一覧から外れません。空リストに直して「誰もいなかった」とすることもできません。

   最小の直し方: `photo_edits` 行がある写真では、遭遇時間からの人一覧を使わず `people` を正とする。部分編集を許すなら、`people` が未設定か明示的な空かを区別する列が必要です。

   試験不足: 現在の試験は人を追加する方向だけで、推定された人を削除・置換する場合を確認していません。

7. **中 — 複数の自分のアカウントを使うと、フレンド判定が別アカウントの出来事で上書きされる**

   場所: [Queries.cs:217](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Queries.cs:217)、[Queries.cs:227](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/Queries.cs:227)

   `friend_state` は `account_id` を捨て、`user_id` ごとの最新イベント1件だけを採用します。

   具体例: 自分のアカウントAではXが現在もフレンド、アカウントBではXを後から外した場合、Bの `Unfriend` が新しいためX全体が `former` になります。逆に、どれか1アカウントの古い状態を別アカウントの新しいスナップショットが覆うこともあります。

   最小の直し方: まず `(account_id,user_id)` ごとに現在状態を決め、その後「いずれかの自分アカウントで現在フレンドならfriend」と集約する。スナップショットがあるアカウントでは現在のスナップショットを優先する。

   試験不足: [ExtensionTests.cs:60](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/ExtensionTests.cs:60) は自分のアカウントが1つだけです。

8. **中 — 重なった訪問・別端末の同時プレイで、ワールド別・人別の結果が誤る**

   場所: [PhotoCatalog.cs:173](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:173)、[PhotoCatalog.cs:200](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/PhotoCatalog.cs:200)

   `ByWorld` は「撮影前に開始した最後の訪問」だけを見ます。訪問Aが10:00–12:00、訪問Bが11:00–11:30で、メタデータなし写真が11:45なら、Bが終わっているため未分類になり、まだ続いているAを探しません。

   `ByPerson` は端末・アカウント・訪問を写真と対応付けず、遭遇時間帯にある全写真へ人を付けます。PC AとPC Bの記録が同じDBにあり、同時刻に別ワールドへいると、PC B側の人がPC Aの写真にも付けられます。

   最小の直し方: 有効な全訪問から候補を選び、`photo_locations.device_id`、写真のワールド情報、訪問の`device_id/account_id`で絞る。候補が複数で決められない写真は推定分類しない。

   試験不足: 訪問の重なり、複数端末、複数アカウントが未試験です。

9. **中 — RemoteThumbsの初回SSH接続は、相手の鍵を確認せず即座に信頼する**

   場所: [RemoteThumbs.cs:79](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/RemoteThumbs.cs:79)、[RemoteThumbs.cs:85](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/RemoteThumbs.cs:85)、[RemoteThumbs.cs:95](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Photos/RemoteThumbs.cs:95)

   保存済み鍵がない初回はTOFUで自動承認し、その接続中に試験コマンドやスクリプトのアップロード・実行まで進みます。利用者へ指紋を見せて確認する段階がありません。

   さらにSSH接続とSFTP接続を別々に張り、初回は両方とも無条件承認します。2接続で異なる鍵が提示されても、`seen` が後の値で上書きされます。

   具体例: 初回接続時に名前解決やLANを操作された場合、偽ホストを保存し、その後も本物のNASを拒否します。

   最小の直し方: 初回の接続は指紋表示だけにして、利用者の明示承認後に保存・コマンド実行する。少なくとも最初のSSH接続で得た指紋を、続くSFTP接続でも必ず照合する。

   試験不足: 現在は生成した公開鍵の形式確認だけで、初回・一致・不一致・SSH/SFTP間の鍵違いを試していません。

## 補足

- `PhotoXmp.Write` は一時ファイルへ書いてから置換しているため、通常のプロセス中断が置換前なら原本は残ります。一方、`fsync`、置換後のPNG再検証、元ファイルのACL・拡張属性の保存はありません。電源断時の破損可能性は環境依存のため**未確認**です。
- TrueNASの短いvolume構文と、NASスクリプトの通常のID計算はC#側と一致しています。PythonとC#はいずれもXMPのIDを先頭1MBから読みます。
- ファイルは変更しておらず、作業ツリーも空でした。
- テスト実行は環境に`dotnet`が存在しないため行えませんでした。静的レビューと既存テストの確認結果です。