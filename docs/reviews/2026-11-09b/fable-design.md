# Yamaori 設計判断レビュー（HEAD 221372f、読み取り専用）

前提: 文書（decisions.md・README・docs/plan.md「残したもの」）と実物を突き合わせた。実物と文書が食い違っていた点は各項に書いた。コードの行番号は /home/mooma-0750/Documents/git-cloned/yamaori 配下。

---

## D1. トークンの範囲 — **確定**（小さな直し 1 つを添える）

- 理由: 実装は文書どおり。`Auth.cs:86-111` で AdminPaths（tokens・password・settings 配下全部＝shutdown/restart/photos/sync も含む）は本人だけ、ScopedPaths で extension/sync の口を限定、`Auth.cs:57-72` で「本人として通るか」を Tailscale/ログイン/この PC で判定し、トークンだけなら `Forbidden` に通す。認証なしの公開は `Access.cs:81-86` で GET/HEAD/OPTIONS のみ。`/mcp` は POST なので認証なし公開では届かない（`Auth.cs:29-32` で /mcp も見張り対象）。
- 実物の補足（文書に書いていない事実）: 「全部」のトークンは `POST /api/people/{id}/forget`（`Api.cs:69`）、`DELETE /api/notes/{id}`（`Api.cs:106`）、そして端末に結び付かないので `POST /api/sync/projection` で**どの子端末の記録でも入れ替え（＝空の投影で消す）られる**（`Api.cs:112-120`、TokenOf が Sync でなければ結び付けを見ない）。「全部」＝「記録を消せる」と理解して渡すこと。
- 変える最小の形: `Auth.cs:254` の `input.Scope ?? TokenScopes.Full` を「scope 必須（無ければ 400）」にする。WebUI は既に extension 既定（`web/src/pages/Settings.svelte:85`）なので影響なし。他のクライアントが黙って全部トークンを作るのを防ぐ。
- 後で足すとよいもの（公開後でも互換）: 読むだけの `read` 範囲（MCP を外から読み専用で使うとき用）。
- 旧トークンが full のまま（`Database.cs:326`）は未公開なので問題なし。
- 戻せるか: はい。確信度: 高。

## D2. 人を忘れる — **変える（小）**。方針は確定でよいが、README の約束と実物に 3 つ穴がある

1. **インスタンスの持ち主としての ID が残る**: `ForgetStore.cs:26-37` は encounters・events・friend_snapshots・notes だけ。`visits.owner_id` と `visits.instance`（`~private(usr_…)` の生文字列）に忘れた人の usr_ が残り、訪問の詳細でそのまま返る（`Queries.cs:93-94` → VisitDetail.OwnerId/Instance）。トリガー（`Database.cs:336-347`）も visits を見ない。
   - 最小の形: Forget に `UPDATE visits SET owner_id = NULL, instance = <owner を伏せた文字列> WHERE owner_id = $k` を足し、visits の AFTER INSERT トリガーで同じことをする。または README.md:65 に「インスタンスの持ち主としての ID は残る」と書く。
   - 戻せるか: 生のログから reparse で戻る。
2. **子の端末には残る**: 同期は子→親の一方向（`SyncWorker.cs`）。親で忘れても子の DB・子の WebUI には残り、子で忘れても親の notes は残る（D7）。README.md:65 の「読み直しても入れない」は「その端末では」の意味であることを明記するか、「忘れるのは親で行い、子は別途」と書く。
3. **忘れた人の一覧（名前つき）が読むだけの口で誰にでも見える**: `GET /api/people/forgotten`（`Api.cs:75`）は AdminPaths にないので、認証なし公開・全トークンから読める（`Auth.cs:105` で token==null は素通り）。忘れたい相手の名前と ID をわざわざ列挙する口なので、`Auth.cs:87` の AdminPaths に `/api/people/forgotten` を足す（prefix 一致なので DELETE も本人限定になる）。ついでに `POST /api/people/{id}/forget` も本人限定にしてよい（Forbidden に `EndsWith("/forget")` の 1 行）。
- 確信度: 高（穴の存在）、中（どこまで消すべきかは本人の価値判断に近い）。

## D3. SFTP の相手の鍵（TOFU） — **確定**（直し 2 つは任意）

- 理由: 実装は文書どおり（`PhotoTargets.cs:108-114` で未知なら受け入れて SeenHostKey、既知と違えば拒否→`TargetRefusedException` でその回全部やめる `PhotoSync.cs:65-66,123-131`）。利用者層（家の NAS）には TOFU が妥当。指紋を貼らせる案は入力の手間に見合わない。
- 実物の注意: (a) `Auth.cs:411-413` は**写真の設定を何か 1 つでも保存すると**（残す日数を変えただけでも）全部の鍵を忘れる。再 TOFU の窓が不必要に開く。最小: Kind/Target/Username のどれかが変わったときだけ消す。(b) 不一致の「警告」はログファイルだけ（`PhotoWatcher.cs:91-94`）。WebUI には出ないので、送れていないことに気づけるのは設定画面の「試す」を押したときだけ。
- 戻せるか: はい（設定の保存で覚え直す）。確信度: 高。

## D4. 日の区切り — **確定（親のタイムゾーン）**

- 理由: 「記録した端末のタイムゾーン」は今のプロトコルでは**実装できない**。子が送るのは UTC の時刻だけで（`SyncStore.cs:49-58`）、`log_files.time_zone` は送られない。やるなら訪問ごとか端末ごとに TZ を運ぶ契約変更になる。旅先の子は稀なケースで、費用に見合わない。
- 代案（追加だけで戻せる）: `/api/days`・`/api/days/{date}`（`Api.cs:24-31`）に `tz` クエリを足し、WebUI がブラウザの TZ を渡す。`Queries` は既に `TimeZoneInfo` を受けられる（`Queries.cs:48-50`）。公開前には不要。
- Docker 親は `TZ` を付け忘れると UTC で区切る（compose.yaml:15 に既定あり、README.md:107 にも書いてある）。
- 確信度: 中。

## D5. 日をまたぐ訪問 — **確定**

- 理由: `Days()`（`Queries.cs:132`）と `Day()`（`Queries.cs:149-153`）が同じ規則（入った時刻の日）で一貫している。VRChat は深夜をまたぐのが普通で、「9 日の夜」として 9 日に付くのは日記としては自然。分けると「その日の合計」と「訪問の時間」がずれて説明が要る。
- 確信度: 高。

## D6. 子にだけ入るもの — **確定（送らない）**。ただし文書の補いが要る

- 理由: `SyncStore.cs:107` は `log:{device}/` だけ受ける。取り込みの出どころは端末を含まない固定文字（`VrcxImport.cs:21` の `"vrcx"`、VAT も同様）なので、子から送ると複数端末の取り込みが同じ出どころでぶつかり、ID 体系の変更になる。今やる価値はない。
- 文書の補い: README.md:106 は「取り込みは親で」の前提だが、VRCX の DB は Windows の子にしかない。Linux の親で使うには「VRCX.sqlite3 を親へ写して `import-vrcx --file`」と手順を書く。
- 後で送るなら `vat:{device}`・`vrcx:{device}` の出どころを足す形で追加可能（互換を壊さない）。
- 確信度: 中。

## D7. メモの消去が親へ届かない／時計ずれ — **変える（小、墓石だけ）**。時計ずれは残す

- 理由: `Queries.cs:330-334` は物理削除、`SyncStore.cs:84-91` は存在する行しか送らないので、子で消したメモは親（VPS かもしれない）に永久に残る。プライバシーを売りにするアプリで、メモは「ログから戻らない唯一のデータ」（README.md:67）。さらに `Queries.cs:323-327` で親が子のメモを直すと device_id は子のまま → 子が後で直すと親の直しを黙って上書き（一方向同期の LWW として仕方ないが、知っておくこと）。
- 最小の形: 移行 12 で `notes.deleted_at TEXT` を足す。`DeleteNote` は deleted_at と updated_at を立てる（一覧・件数は `deleted_at IS NULL` で絞る）。`SyncNote` に `DeletedAt` を足し、`ApplyNotes` の UPDATE に含める。古い子は DeletedAt を送らない＝null なので互換。
- 時計ずれ（親が版を振る）は作り込みが大きく、NTP の前提で残してよい。
- 戻せるか: はい（追加だけ）。公開後に足しても互換だが、親が VPS の人には公開前が望ましい。確信度: 中。

## D8. データのフォルダを写した 2 台 — **確定（文書で注意）**。実害は文書より小さい

- 理由: 端末 ID は DB 内（`Database.cs:352-365`）。ただし出どころはログのファイル名を含む（`LogProjector.cs:150`）ので、「上書きし合う」のは VRChat を同じ秒に起動したときだけ。実害は「2 台が 1 台として扱われる」。さらに Windows では DPAPI（`Secrets.cs:15,23`）のため、別 PC・別ユーザーへ写した設定の親トークンは復号できず、同期自体が動かない（→ D18 で起動すら止まる）。
- 最小の形: README に「子を増やすときはデータのフォルダを写さない（DB を消して起こす）」の 1 行。端末ごとの鍵は、復元（同じ本人・新 PC）で逆に端末が割れる副作用があり、今は要らない。
- 確信度: 中。

## D9. 設定の置き場所 2 か所 — **確定**。ただし書き込みの形を直す（公開前）

- 理由: 分け方自体（起こし直しが要るものは yamaori.json）は妥当。問題は書き方。`Auth.cs:343-352`、`395-410`、`481-494` の 3 か所が別々に read-modify-write し、`File.WriteAllText` で直接書く。電源断・同時保存で yamaori.json が壊れると、`Program.cs:29-32` の `AddJsonFile(optional: true)` は「無い」は許すが「壊れている」は例外 → **Yamaori が起動しなくなる**（窓がないので無言）。ここには親トークンと NAS のパスワードも入っている。
- 最小の形: 1 つのヘルパーに集約し、`.tmp` に書いて `File.Move(overwrite: true)`、プロセス内ロックで直列化。起動時は壊れた json を `.broken-<時刻>` に退避して既定で起きる。
- 戻せるか: はい。確信度: 高（起きたときの被害）、中（起きる頻度）。

## D10. 「保存」の一覧の名前が日本語固定 — **確定（今は）**

- 理由: `Api.cs:53-54`、`Queries.cs:343,351` で表示名そのものを `world_favorites.list_name` に入れている。VRCX のグループ名も同じ扱い（`VrcxImport.cs:195`）なので、一貫している。後で鍵と表示名を分けるときは `UPDATE … WHERE source='yamaori'` の 1 回の移行で足りる。
- 戻せるか: はい。確信度: 高。

## D11. 写真は名前と大きさで見分ける — **確定**

- 理由: `PhotoCatalog.cs:72` の ID、`PhotoSync.cs:91-96` で消す前に中身の違う同名同サイズを検出して消さない。VRChat の写真名はミリ秒の時刻と解像度を含むので衝突は実質ない。中身のハッシュに替えると NAS の全写真（GB 単位）を一度読む必要があり、費用に見合わない。
- 確信度: 高。

## D12. ログイン失敗の全体ロック — **確定**

- 理由: `Auth.cs:157-179,196-203`。LAN の攻撃者が 10 分に 31 回で本人を 5 分締め出せるが、それだけ（DoS）。listen=tailscale の本人はヘッダーで通るので無関係。per-IP だけだと XFF で回避されるので全体ロックは要る。
- 確信度: 高。

## D13. 子から送る量の上限／日の一覧の全読み — **確定（今は）**

- 理由: `Api.cs:112-120` に件数上限なし、Kestrel 既定 30 MB。被害はトークン保持者による肥大化だけ。`Queries.cs:125-128` は全訪問＋全 encounters を毎回メモリへ読む。数年分（encounters 数十万）でトップ画面の表示が秒単位になる見込み。
- 後で: Days を SQL 集計に、projection の配列件数（例 5 万）と文字列長に上限。公開前には不要。
- 確信度: 中。

## D14. Tailscale のヘッダーを loopback なら信じる — **確定**（README に条件を 1 行）

- 理由: `Access.cs:56-61` は「loopback かつヘッダーあり かつ listen=tailscale」のときだけ本人扱い。脅威は「この PC に別の loopback プロキシがあり、外のヘッダーをそのまま転送する」構成だけ。tailscale serve 自身は受信側の `Tailscale-User-*` を消してから付け直すと認識している（本レビューでは未検証）。
- 最小の形: README.md:109 付近に「listen=tailscale のときは、この PC に tailscale serve 以外のプロキシを置かない」。
- 後で固めるなら: tailscale serve 専用の第 2 の loopback ポートを開き、そのポートに来た要求だけヘッダーを読む（Server.cs・Auth.Describe・README の 3 か所）。
- 試験の穴: HTTP レベルの試験に `Tailscale-User-Login`・`X-Forwarded-*` を付けたものがない（tests/ を grep、AccessTests の record 直叩きのみ）。
- 確信度: 中。

## D15. 拡張機能をストアへ／CI／CHANGELOG — **ストアは本人にしか決められない。CI と CHANGELOG は足す**

- ストア: 開発者アカウント・審査・hook.js がページの通信を読む点の説明が要る。本人の時間と規約判断。
- CI: `dotnet test` と `npm run build` を回す 1 ファイル。公開するなら PR の最低限の網。
- CHANGELOG: README の「作っているところ（日付）」は版を重ねると持たない。版ごとの DB 移行（`.bak-v<版>`）の説明先にもなる。
- 確信度: 高。

## D16. GitHub へ上げるか — **本人にしか決められない**（勧め: 上げる前の確認 3 点）

- 確かめた: 試験の fixture は匿名化済み（`tests/Yamaori.Tests/Fixtures/output_log_2026-11-08_02-36-41.txt` は usr_0000… と Alice）、docs/log-check に usr_ ID なし。
- 未確認（公開前に本人が見るもの）: git の履歴に生のログや実名が入っていないか、依存（SSH.NET など）のライセンス表記、plan.md:117 の商標。

## D17. reparse の非原子性ほか — **確定**（小さな直し 1 つは任意）

- 理由: `LogIngestor.cs:29-37` は `Delete(name)` が自分の tx で commit してから `Ingest` が別 tx。間で落ちると生の log_events が消え、古い投影が残るが、アーカイブから reparse し直せば戻る。通知の二重所有（`ProjectionWriter.cs:55-60`、ID は通知 ID `LogProjector.cs:113`）も reparse で戻る。同じ長さで中身だけ替わるログは VRChat のファイル名が秒まで含むので実質ない。
- 任意の直し: Delete を Ingest の tx に入れる（10 行程度）。
- 確信度: 高。

---

## 追加（文書に挙がっていないが今決めるべきもの）

## D18. DPAPI の復号失敗で Yamaori が起動しない — **変える（公開前・必須）**

- 何が起きるか: `Secrets.cs:23` の `ProtectedData.Unprotect` は別ユーザー・別 PC・壊れた blob で `CryptographicException` を投げる。`SyncWorker.cs:22` はこれを try の外で呼ぶ → `ExecuteAsync` が失敗 → .NET 既定（StopHost、上書き設定なし）でホストごと止まる。窓もトレイもないので無言で死ぬ。
- 再現条件: 子の設定で親を入れた状態のデータのフォルダを、PC を買い替えて復元／Windows のユーザーを作り直して復元／D8 のように別 PC へ写す。README.md:66 の「戻す」手順は DB だけだが、実際はフォルダごと戻す人が多い。
- 最小の形: `Secrets.Unprotect` で `CryptographicException`・`FormatException` を捕まえて null を返し、SyncWorker/PhotoWatcher で「設定の秘密を読めない。設定で入れ直す」と警告ログ。`Auth.cs:360` の /test も同じ経路で 500 になるので直る。
- 戻せるか: はい。確信度: 高（経路）、中（StopHost の既定は .NET 6 以降の挙動として記憶で判断）。

## D19. sync トークンが、入力を確かめる前に端末へ結び付く — **変える（公開前・小）**

- `Api.cs:115-117`: `BindTokenDevice` → その後 `Apply` が「親そのもの」「出どころの形が違う」で拒否。拒否されてもトークンは最初に名乗った端末 ID に結び付いたまま → 以後その子（正しい ID に直した後も）は 403「ほかの端末に結び付いている」でトークン作り直し。D8 のフォルダ複製と組み合わさると確実に踏む。
- 最小の形: is_self と出どころの形の検査を結び付けの前に出す（または Apply 成功後に結び付ける）。`/sync/notes` 側（`Api.cs:130-132`）は既に parentSelf を見ているので、projection 側だけ。
- 戻せるか: はい（トークンを消せば）。確信度: 高。

## D20. 親子の版の違いに対する取り決めがない — **本人が今決める（規則だけ。実装は小）**

- 公開後、親（VPS）と子（Windows）は別々に更新される。`SyncProjection`（`SyncStore.cs:10-13` と Records の形）が契約。System.Text.Json は項目の追加には寛容だが、削除・改名・意味の変更は黙って欠けるか 400 になる。
- 決めること: 「親は子と同じか新しい版」を規則にする。実装は、子が送る前に `GET /api/status` の Version（既にある `Api.cs:14,21`）を見て、親が古ければ送らず警告する 10 行。将来の互換の切れ目を検出できるよう `Status` に `Protocol` 整数を 1 つ足しておく（追加だけ）。
- 戻せるか: はい。確信度: 中。

---

## 公開の前に必ず要るもの（順）

1. **D18** Unprotect の例外を捕まえる（復元で無言に起動しなくなる）
2. **D9** yamaori.json の書き込みを原子的に・直列に（同じ「起動しない」系、秘密を含む）
3. **D19** sync トークンの結び付けを検査の後に
4. **D2** `visits.owner_id/instance` の扱いを決めて README に合わせる（消すか、残ると書く）／`/api/people/forgotten` を本人限定に／「忘れるは端末ごと」の明記
5. **D20** 親子の版の規則を README に書き、子に親の版の確認を入れる
6. **D1** API のトークン作成で scope を必須に（1 行）
7. **D14・D6・D8** README の注意書き（各 1〜2 行）

公開後でよいが早めに: D7（メモの墓石）、D15（CI・CHANGELOG）、D3（鍵を忘れる条件を絞る）、D17（Delete を同じ tx に）。

## 見なかった範囲

web/ の画面コード（scope の既定値の grep のみ）、extension/、installer/、LogWatcher の全文、VatImport、試験の中身、git の履歴 — 判断一覧に直接関わる箇所だけを読む方針のため。
