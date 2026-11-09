指定差分を読み取り専用でレビューしました。重大度順です。

## 指摘

1. **高 — メンバー用トークンで取り込み記録を任意に入れ替えられる**

場所: [ImportSettings.cs:38](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/ImportSettings.cs:38)、[Auth.cs:101](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/Auth.cs:101)、[ImportJobs.cs:94](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/ImportJobs.cs:94)

具体例: `sync` スコープのトークンを持つメンバーが、役をすべて外された後でも、次を送れます。

```text
POST /api/sync/import/vrcx?replace=true
Authorization: Bearer <member token>
本文: 攻撃者が作った有効な VRCX DB
```

エンドポイントはトークンの `DeviceId`、現在のグループ所属、`vrcx` の役を確認しません。また `replace=true` もそのまま受け入れます。結果として、既存の `source='vrcx'` の訪問・出来事・メモなどを攻撃者のDBで置き換えられます。手動で作った未バインドの `sync` トークンでも実行できます。

最小の直し方: `Auth.TokenOf(ctx)` から、バインド済み `DeviceId`、有効な `group_members` 行、種類に対応する役を必須にする。メンバートークンからの `replace=true` は拒否し、入れ替えはまとめ役の画面で承認する。

2. **高 — 8 GiB の本文をジョブ確保前に保存し、切断時の一時ファイルも残る**

場所: [ImportSettings.cs:16](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/ImportSettings.cs:16)、[ImportSettings.cs:42](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/ImportSettings.cs:42)、[ImportJobs.cs:37](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/ImportJobs.cs:37)、[ImportJobs.cs:63](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/ImportJobs.cs:63)

具体例: 取り込みジョブが既に動いている間に、複数のメンバーが8 GiBずつ送ります。全本文を別々の一時ファイルへ書き終えてから `Start()` が競合を検出するため、同時アップロード数だけディスクを消費します。途中でクライアントが切断して `CopyToAsync` が失敗すると、部分ファイルは削除されません。

結果: データディスク枯渇、DB書き込み失敗、残存一時ファイルの蓄積。

最小の直し方: 保存前に取り込み用セマフォを取得し、空きがなければ本文を読む前に409を返す。コピー処理を `try/finally` にして失敗時も削除する。実コピー側にも独立したバイト上限を設け、起動時に古い `import-tmp` を清掃する。

3. **高 — 正常なメンバー取り込みでも、まとめ役の記録として扱われ、複数端末で衝突する**

場所: [ImportJobs.cs:94](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/ImportJobs.cs:94)、[ImportJobs.cs:100](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/ImportJobs.cs:100)、[VrcxImport.cs:155](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VrcxImport.cs:155)、[VatImport.cs:128](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/VatImport.cs:128)

具体例: メンバーAがVRCX DBを送ると、取り込み先では `state.DeviceId`、つまりまとめ役の端末IDで保存されます。そのためログとの重複判定もAのログではなく、まとめ役のログに対して行われます。

さらに `vrcx` と `vat` の source/origin は端末別でありません。メンバーAとBの両方に `vrcx` の役を付けると、Bの自動取り込みは「別のDB」として止まります。手動で入れ替えるとAの取り込み分が消えます。

最小の直し方: 認証トークンの `DeviceId` を `ImportJobs` と importer に渡す。source、origin、置換範囲を端末別にする（例: sourceは表示上そのままでも、置換キーを `(source, device_id)` にする）。複数端末を許さない設計なら、`vrcx` 役をグループ内で一台だけに制限する。

4. **中 — 役を外すと、既に集めた未送信ログまで送られなくなる**

場所: [SyncWorker.cs:32](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/SyncWorker.cs:32)、[SyncWorker.cs:39](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/SyncWorker.cs:39)、[SyncWorker.cs:79](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/SyncWorker.cs:79)

具体例: メンバーがログを収集した直後、同期前にまとめ役が `logs` の役を外します。次の同期は先に新しい役を読み、その直後の `PushAsync(... sendLogs: false)` で既存の未送信ログも除外します。

結果: ローカルDBには残りますが、まとめ役の記録は欠けたままになります。役を再度付けるまで送られません。

最小の直し方: `logs` は「新しく収集するか」だけに使い、既に存在するローカルソースは常に同期する。あるいは役が true→false に変わる際に、最後の同期を完了してから新しい役を確定する。

5. **中 — まとめ役から外された端末が古い役のまま動き続ける**

場所: [SyncWorker.cs:66](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/SyncWorker.cs:66)、[DeviceRoles.cs:48](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/DeviceRoles.cs:48)

具体例: 端末の役が `vrcx` のみになった後、まとめ役から端末そのものを外します。役の取得は401になりますが、ローカルの `group_roles` とグループ設定は消えません。

結果: 端末はログ・写真を収集しない状態を維持し、VRCXは収集または送信を試し続けます。送信はすべて401になり、本人が気付いて手動で抜けるまで、端末単体としての通常収集にも戻りません。

最小の直し方: `/api/sync/group` の401が一定回数続いたら安全側へ倒し、少なくとも全ローカル収集を再開する。可能なら「除名済み」を表す短寿命の失効応答を返し、端末側でグループ設定を解除する。

6. **中 — 端末IDは自己申告で、再参加時の端末所有確認がない。並行参加では消せないトークンも残る**

場所: [GroupStore.cs:90](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/GroupStore.cs:90)、[GroupStore.cs:95](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/GroupStore.cs:95)、[GroupStore.cs:102](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/GroupStore.cs:102)、[GroupStore.cs:130](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori.Store/GroupStore.cs:130)

具体例:

- 招待コードを持つ相手が、既存メンバーの `DeviceId` を指定すると、その端末の旧トークンを失効させ、新しいトークンを取得できます。
- 同じ `DeviceId` と異なる有効な招待コード2個をほぼ同時に送ると、招待消費・旧トークン削除のトランザクションと、新トークン作成・メンバー更新が分離されているため、2個の有効なトークンが作られる可能性があります。`group_members.token_id` に最後の一個しか残らず、メンバー削除後も片方が有効なままになります。

最小の直し方: 既存 `DeviceId` の再参加には旧トークンまたは端末鍵による証明を必須にする。招待消費、旧トークン削除、新トークン挿入、バインド、メンバー更新を同じトランザクションに入れる。端末に属する全トークンを削除できるようにもする。

7. **中 — HTTP接続を許すため、招待応答と以後のBearerトークンが平文になる**

場所: [GroupApi.cs:134](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/GroupApi.cs:134)、[GroupApi.cs:140](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/GroupApi.cs:140)、[GroupApi.cs:150](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/GroupApi.cs:150)

具体例: `HubUrl=http://192.168.1.10:47800` で参加すると、返却されるメンバートークンと、その後の同期・DB取り込みが同一LAN上で盗聴可能です。盗んだトークンはBearerなので再利用できます。

Tailscale IPへの `http://100.x.x.x` はTailscale内で暗号化されますが、手入力された通常LANアドレスとの区別はありません。

最小の直し方: 原則HTTPSを必須にする。Tailscale IPのHTTPだけを明示的に許可するか、通常HTTPでは危険性を表示して追加確認を必須にする。

8. **中 — 未認証の30回で、すべての参加を継続的に止められる**

場所: [GroupApi.cs:35](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/GroupApi.cs:35)、[GroupApi.cs:84](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/GroupApi.cs:84)

具体例: LAN/Tailnet上の未認証相手が、10分以内に30回でたらめなコードを送ると、それ以降は正しい招待コードも429になります。攻撃者が間隔を空けて送り続ければ参加を止め続けられます。

合言葉自体は約50ビットあり、一度きり・30分なので、強さは十分です。問題は全接続元で共有する回数制限です。

最小の直し方: 接続元IP単位の制限を中心にし、全体制限は大きめのトークンバケットと同時検証数制限にする。プロキシヘッダーを信用する範囲も既存ログイン処理とそろえる。

9. **中 — 別の取り込み中だっただけで、VRCX自動取り込みをその更新時刻について永久停止する**

場所: [ImportJobs.cs:73](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/ImportJobs.cs:73)、[ImportJobs.cs:206](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/ImportJobs.cs:206)

具体例: VATの手動取り込み中にVRCX自動取り込み時刻が来ると、`RunNow()` は実行せず、現在動いているVATジョブの状態（`Ok=null`）を返します。VRCX workerはこれを失敗と扱って、そのVRCX DBのmtimeを `vrcx_auto_blocked` に保存します。

結果: VRCX DBが再び更新されるまで、その内容は一度も取り込まれません。

最小の直し方: `RunNow` に `Busy` を明示した結果を追加し、Busy時はblockedを設定せず次回再試行する。

10. **中 — 別グループへ直接入り直すと、前のグループに有効な所属とトークンが残る**

場所: [GroupApi.cs:131](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/GroupApi.cs:131)、[GroupApi.cs:146](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/GroupApi.cs:146)、[GroupApi.cs:158](/home/mooma-0750/Documents/git-cloned/yamaori/src/Yamaori/Web/GroupApi.cs:158)

具体例: グループAのメンバー状態のまま、グループBのURLと招待コードで `/join` を呼ぶと、ローカル設定だけがBで上書きされます。Aには古いメンバー行と有効なトークンが残ります。`/leave` もまとめ役へ失効を通知しません。

最小の直し方: 既に `Role=child` または `GroupId` がある場合はjoinを拒否し、先にleaveを要求する。leave時は旧まとめ役へ署名付きで削除を依頼し、失敗時は「まとめ役側で外す必要がある」と明示する。

## 試験で守れていない箇所

- [HttpGroupTests.cs:50](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/HttpGroupTests.cs:50) は、壊れた3バイトのDBをメンバートークンで送って「200なら成功」としています。役なし、未バインドトークン、`replace=true`、別端末ID、非まとめ役への送信を拒否する試験がありません。
- [HttpGroupTests.cs:64](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/HttpGroupTests.cs:64) は単一トークンの削除しか確認しておらず、同一DeviceIdの並行acceptと、再参加による乗っ取りを検出できません。
- [HttpImportTests.cs:40](/home/mooma-0750/Documents/git-cloned/yamaori/tests/Yamaori.Tests/HttpImportTests.cs:40) には、途中切断、上限超過、同時アップロード、ジョブ競合時の一時ファイル清掃の試験がありません。
- `DeviceRoles`、`SyncWorker`、`VrcxWorker` について、役の追加・削除、除名時の401、取り込みジョブ競合を通した試験がありません。
- `GroupDiscovery` には、別UserID・offline peerを除外することや、候補へ認証情報を送らないことを固定する試験がありません。

Tailscale探索そのものは、現状では「同じUserIDかつOnlineのpeerへ、秘密を含まない `/api/group/hello` GETだけを送る」実装で、この点に直接の漏えいは見つかりませんでした。

ファイルは変更していません。書き込みを避けるためビルド・テストは実行せず、`git diff --check` と静的な経路追跡のみ行いました。