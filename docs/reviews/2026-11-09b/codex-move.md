指定差分を読み取り専用でレビューしました。重大度順です。

## 指摘

### 1. 高 — 取り込み失敗でも「忘れた人」の削除だけ確定する

場所: `src/Yamaori.Store/MoveImport.cs:37-45, 53-138`

具体例:

1. 送信 DB の `forgotten_people` に `usr_victim` を入れる。
2. `encounters` を欠落させる、または列を壊して後半の SQL を失敗させる。
3. `ForgetStore.Forget()` は取り込みトランザクションより前に個別コミットされる。
4. API は「取り込めなかった」と報告する一方、まとめ役にあった `usr_victim` の遭遇・イベント・メモ等は既に消えている。

悪意あるメンバー DB なら、意図的に「削除だけ成功、取り込みは失敗」を作れます。

最小の直し方:

- 全スキーマ検証を先に終える。
- 忘れる処理を含む全変更を一つのトランザクションに入れる。
- `ForgetStore.Forget` に既存トランザクションを受け取る版を用意するか、同等の削除を `MoveImport` のトランザクション内で行う。

試験の穴:

- `forgotten_people` の適用後に、後続テーブルの欠落・型不正・制約違反で失敗させ、まとめ役が完全に元へ戻る試験がありません。
- 複数人の途中で失敗するケースも未試験です。

---

### 2. 高 — `user_version` と4表の名前だけでは、悪意ある SQLite を Yamaori DB と判定できない

場所: `src/Yamaori.Store/MoveImport.cs:23-35, 47-52, 67-86, 116-136`

具体例:

- `PRAGMA user_version=17` を偽装する。
- `visits` の列順だけを変え、値の型が制約を通るように作る。
- `INSERT ... SELECT *` により、`world_id`、`instance`、時刻などが別の列へ静かに入ります。
- 検査対象外の `encounters`、`events`、`notes`、`friend_snapshots` などはビューにもできます。ビューの評価で極端に大きい結果や高負荷な式を発生させられます。
- `forgotten_people` は `trusted_schema=OFF` より前に別接続で読まれており、表であることすら確認されていません。

添付 DB のトリガーは `mv` 側へ書き込まないため通常は発火しません。また `trusted_schema=OFF` は危険なスキーマ内関数への対策になりますが、列順、組み込み機能だけの高負荷ビュー、偽造スキーマは防ぎません。

最小の直し方:

- `SELECT *` を廃止し、全テーブルで明示的な列リストを使う。
- `sqlite_schema` と `PRAGMA table_xinfo` で、使用する全テーブルについて「実表であること・列名・順序・型・NOT NULL・PK」を期待値と比較する。
- より安全なのは、送信 DB を読み取り専用接続で読み、型を検査した値をパラメーター付き SQL で本 DBへ入れることです。
- `ATTACH` を残す場合も、送信 DB を明示的に read-only/immutable で添付する。

試験の穴:

- 同じ `user_version` で列順だけ違う DB。
- 必要な表をビューに置き換えた DB。
- 必要な表の欠落、余分な列、型・PK・NOT NULL の違い。
- 添付 DB 内のトリガーが発火しないことと、まとめ役側トリガーだけが期待どおり動くこと。
- 壊れたスキーマや循環ビューへの耐性が未試験です。

---

### 3. 高 — まとめ役で削除・復元した状態が、再送で戻る

場所: `src/Yamaori.Store/MoveImport.cs:74-121`

具体例:

1. 1回目の移動でアルバム「夏」、お気に入り、写真編集、ゴミ箱を取り込む。
2. まとめ役でアルバムを削除、お気に入りを解除、写真編集を消す、写真をゴミ箱から戻す。
3. メンバー側に古い状態が残ったまま再度 `/api/settings/import/move` を実行する。
4. アルバム・お気に入り・写真編集・ゴミ箱状態が再作成されます。

メモだけは `note_deletions` を見ています。「同じ写しを直後に何度送っても二重にならない」は満たしますが、「まとめ役で削除した後にも戻らない」は満たしません。忘れた人は `forgotten_people` により戻りません。

最小の直し方:

- この機能が一度限りの移行なら、まとめ役に「この device_id の move は完了済み」を保存し、成功後の再実行を拒否するのが最小です。
- 差分移行を続けるなら、アルバム、お気に入り、写真編集、ゴミ箱等にも削除墓石と更新時刻が必要です。

試験の穴:

- 1回取り込み後にまとめ役で各対象を削除・復元し、同じ端末から再送する試験がありません。
- メンバーとまとめ役の双方で変更された競合ケースも未定義です。

---

### 4. 中 — ID 衝突時に既存行の `device_id` を書き換える

場所: `src/Yamaori.Store/MoveImport.cs:67-72`

具体例:

- まとめ役に `visits.id='v_x'`, `device_id=NULL` の行がある。
- 送信 DB に同じ `id='v_x'` の別行を入れる。
- `INSERT OR IGNORE` は衝突して無視されます。
- 続く `UPDATE ... id IN (SELECT id FROM mv.visits)` が、元からあったまとめ役の行を送信メンバーの `device_id` に変更します。

`encounters` と `events` も同様です。悪意ある DB なら既知の ID の帰属を変更できます。

最小の直し方:

- 後続 `UPDATE` をなくす。
- 明示列の `INSERT` で `device_id` に `coalesce(mv.device_id, $d)` を指定する。
- 既存行を更新する必要があるなら、実際に今回挿入した ID だけを `RETURNING` 等で保持する。

試験の穴:

- 同じ ID の既存行があり、その `device_id` が NULL のケースがありません。
- 同じ ID で内容が異なるケースも未試験です。

---

### 5. 中 — 8 GiB と無制限 SQL 評価により、メンバートークンだけで可用性を落とせる

場所: `src/Yamaori/ImportJobs.cs:33, 42-59`、`src/Yamaori.Store/MoveImport.cs:27, 40, 67-136`

具体例:

- メンバーが最大8 GiBの SQLite を繰り返し送る。
- `quick_check` はファイル全体を読み、取り込み SQL に行数・実行時間・SQLite VM ステップ数の上限がありません。
- 小さい DBでも、検査されていないビューから大きな直積を返せます。
- 一度に一件へ制限されているため並列のディスク枯渇は抑えられますが、完了・失敗後の再送は可能です。

最小の直し方:

- `move` 専用の、実データに即した小さい上限を設ける。
- DBのページ数、表ごとの行数、文字列・BLOB長を取り込み前に制限する。
- 可能なら SQLite の progress handler/interrupt と処理期限を設ける。
- 端末ごとの再試行制限も加える。

試験の穴:

- 上限近くの DB、極端な行数、巨大文字列、ビューによる行数増幅、キャンセル時の後始末が未試験です。

---

### 6. 中 — 時刻が単なる文字列比較なので、不正値が恒久的に「最新」になれる

場所: `src/Yamaori.Store/MoveImport.cs:115-120, 125-136`

具体例:

- `photo_edits.edited_at='zzzz'` を送ると、正しい ISO UTC 時刻より辞書順で新しい扱いになります。
- 以後、正常な写真編集では上書きできません。
- `friend_snapshot_meta.taken_at` も同様です。

最小の直し方:

- 取り込み前に全時刻を `Database.ParseTime` 相当で厳密に検証し、UTC正規形以外は DB 全体を拒否する。
- 比較だけ `julianday()` にする方法では、不正値を新規挿入するケースが残るため、入力検証が必要です。

試験の穴:

- 不正時刻、タイムゾーン付き表記、小数秒、同時刻で内容が違う場合の勝者が未試験です。

---

### 7. 低 — Tailscale の永続状態を使う構成なのに `TS_AUTH_ONCE=true` がない

場所: `deploy/truenas/yamaori.yaml:27-32`

公式仕様では `TS_AUTH_ONCE` の既定値は false で、起動ごとに `tailscale up` を行います。YAML の説明は「一度入れば鍵を消してよい」としているため、永続状態を再利用する意図なら `TS_AUTH_ONCE: "true"` を明示した方が一致します。[Tailscale Docker parameters](https://tailscale.com/docs/features/containers/docker/docker-params)

具体的な結果:

- 単発の auth key を残したまま再起動すると、期限切れ・使用済みキーを再提示して起動に失敗する版や条件の影響を受けやすくなります。
- 鍵を消した後に既存状態だけで再起動する挙動も、現在の `containerboot` 実装へ余計に依存します。

最小の直し方:

```yaml
TS_AUTH_ONCE: "true"
```

試験の穴:

- 初回対話ログイン、単発キー、キー削除後、コンテナ再起動・再作成の4経路が未確認です。
- TrueNAS 実機での host path 権限も未確認です。

## 認証と Compose について確認できたこと

- `--Listen lan` なので、Serve が付ける `Tailscale-User-Login` は自動的な本人認証には使われません。`AccessPolicy` は `Listen == "tailscale"` のときだけ許可リストのログイン名を本人扱いします（`src/Yamaori.Core/Access.cs:55-60`）。したがって現在の YAML では、tailnet の別ユーザーはパスワード・Cookie・トークンなしに本人扱いされません。
- Tailscale Serve はクライアントが送った `Tailscale-User-*` ヘッダーを除去してから正しい値を付ける仕様です。[Tailscale Serve identity headers](https://tailscale.com/docs/features/tailscale-serve)
- `docker compose config` は成功しました。`network_mode: service:tailscale`、ポートを tailscale 側に置くこと、Yamaori の `command` とイメージの `ENTRYPOINT` の組み合わせは整合しています。
- `$${TS_CERT_DOMAIN}` は Compose 展開を避け、serve.json に `${TS_CERT_DOMAIN}` を残す正しい形です。`containerboot` がそのプレースホルダーをFQDNへ置換します。[containerboot source](https://github.com/tailscale/tailscale/blob/main/cmd/containerboot/main.go)
- 実際のコンテナ起動、証明書発行、TrueNAS固有のYAML受付・ディレクトリ権限は未確認です。

既存試験は正常系の一回目、直後の同一再送、版違い、HTTP認証を押さえていますが、悪意あるスキーマ、途中失敗時の原子性、まとめ役で削除した後の再送が大きく抜けています。テスト実行は環境に `dotnet` がなく未実施です。ファイルは変更していません。