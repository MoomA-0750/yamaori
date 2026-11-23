# VRCX との連携を深める（2026-11-23）

## tofu の VRCX の実データ（読むだけで調べた）

- VRCX 2026.11.14、DB 版 18（`configs` の `config:vrcx_databaseversion`）、journal_mode=delete
- 2026-11-23 に入れたばかりで 0.5 MB。gamelog 系はどれも 0 件。フレンドの一覧 40、VRChat のノート（`<P>_notes`）9、フィード数件
- アカウントごとの表の頭 `<P>` は `usr` + ID の 32 桁の 16 進数（ハイフンと _ を除いた形）
- `configs` には `config:lastuserloggedin`（usr_…）、`config:friendloginit_usr_…`、`config:savedcredentials`（ログイン情報）がある。`cookies` 表もある

## 足りなかったもの

1. アカウントの ID を gamelog_join_leave と通知の受け手からしか拾わないので、入れたばかりの VRCX では空。フレンドの一覧が入らず、origin が "@" になり、遊んだあとに "usr_…@時刻" へ変わると別の DB とみなして止まる
2. メンバーが送る写し（VACUUM INTO で丸ごと）に、cookies とログイン情報が入る
3. VRCX はフィードで常に書くので mtime が毎回変わり、そのたびに DB 全体を送る
4. メンバーは送れたら成功とみなし、まとめ役での取り込みの成否を知らない
5. 取り込んでいない: VRChat のノート（`<P>_notes`）、`<P>_notifications_v2`

## 決めたこと（仮。確認一覧に積んだ）

- 送る写しは許可した表だけ（禁止する表を並べる形にしない。VRCX が後で秘密の表を足しても漏れない）
  - gamelog_location・gamelog_join_leave・gamelog_video_play・cache_world・favorite_world・world_memos・memos
  - 頭ごとの `_friend_log_history`・`_friend_log_current`・`_notifications`・`_notifications_v2`・`_notes`
  - configs は `config:lastuserloggedin`・`config:friendloginit_%`・`config:vrcx_databaseversion` の行だけ
- 変化は中身の指紋で見る（gamelog は件数・max(id)・location の sum(time)、ほかは中身のハッシュ、先頭に許可一覧の版）
- フィード（位置・オンライン・ステータス・bio・アバター）、moderation、favorite_friend、avatar_history は入れない（他人の動きの記録・入れ先が無い）
- VRChat のノートは VRCX のメモと別の行、出どころ `vrchat-note`。どちらも正にしない。Yamaori で直したら Yamaori のもの（source NULL）になり上書きしない
- 真の差分同期（行だけ送る）はやらない。絞った写しが大きすぎたら考え直す
- origin は vrcx では「前のアカウントが空か同じ、かつ前の最古が空か同じ」なら同じ DB とみなす
- まとめ役は、メンバーから来た vrcx の結果を `import_last:vrcx:<deviceId>` に残し、`GET /api/sync/import/vrcx/result` で返す。古いまとめ役では 404 →「分からない」
- 同期の版（SyncProtocol 3）と `/api/sync/import/vrcx` の形は変えない。古いまとめ役＋新しいメンバー、新しいまとめ役＋古いメンバーのどちらでも動く

## やらないこと（記録だけ）

- 出どころ "vrcx" が全端末で一つ。2 台目の端末が VRCX を送るとぶつかる（役は 1 台だけなので今は起きない）
- VRCX でメモを消しても Yamaori のメモは消えない（前から）

## 遊んだあとの実データで確かめたこと（2026-11-23 17:38 に tofu で一度遊んだあと）

- VRCX に訪問 2・入った/出た 各 5（4 人、うち 2 件は自分）・動画 2 が入った。同じ時間の VRChat のログもある
- tofu の中に試しのまとめ役とメンバーを立て、遊ぶ前の状態（origin「アカウント@」、ノートあり）を作ってから、実 VRCX を自動で送った
  - 止まらずに取り込めた（origin は「アカウント@最初の訪問」へ）。訪問 2（アカウント付き）・会った人 3（自分は外れる）・動画 2
  - メンバーは結果を読んで「まとめ役で取り込んだ」と残した
- 見つけた問題: VRCX を先に取り込み、あとから同じ時間のログが届くと、同じ訪問・会った人・動画を両方に持つ（重なり 2 組）。VRCX は中身が変わったときだけ取り込み直すので、直らないまま残る
  - 直し（c20d4e5）: ログのセッションを書くとき（手元のログ・メンバーからの同期の両方）に、その端末の取り込み（VAT・VRCX）の訪問のうち、その期間に入ったものと、その会った人・出来事・期間の動画を消し、期間にかかるものは始まりで切る（取り込みで飛ばす・切る決まりと同じ）
  - 直した版で同じ順に通し、重なりは 0。そのあと取り込み直しても「訪問 0・ログと重なって飛ばした訪問 2」で同じ結果
- もう一つの直し（85f08a4）: メンバーが送ったあと、結果を読む前に起こし直すと結果を読みに行かなかった。待っている印を DB に持つ
