# 引き継ぎ（今の状態）

最終更新: 2026-11-18

## 目的

VRChat の記録を残す Yamaori を、TrueNAS を「まとめ役」（記録を全部持ち WebUI を配る）、Windows の to-fu を「メンバー」（記録を送る）にした端末のグループで使えるようにする。

## 今どこまでできたか

- origin の main と kasane-trial は e2e521b。CI と image（ghcr.io/mooma-0750/yamaori:latest）は成功
- この HANDOFF.md を足した commit は手元の kasane-trial にだけある。push は本人が頼んだときだけ、という決まりのため
- TrueNAS のまとめ役は e2e521b の版で動いている（2026-11-18 に本人が起こし直した）。to-fu はトークンでつながっている（トークンの最後に使った時刻が 11/18 13:23）
- to-fu の Yamaori は 0.1.10。アカウントの名前もまとめ役へ送り済みで、まとめ役の「端末とアカウント」に名前で出る
- 画面は kasane の見た目へ移し終えた。設定のトークンの「消す」は枠付き、端末とアカウントは一つの一覧、一覧と入力欄の間は 20px
- NAS の写真の縮小（約 2.8 万枚）は TrueNAS 側で作ってある
- 確認一覧（phase-board の check）に、このプロジェクトの未完了は無い

## 決まったこと（会話にしか無かったもの）

- 受け取る量の上限
  - まとめ役へ移す写し（move）: 1 GiB（`ImportJobs.MaxMoveUpload`）
  - VAT・VRCX の取り込み: 8 GiB
  - 普段の同期: Kestrel の既定の約 30 MB のまま（D13）
  - 上限はメモリではなく、まとめ役のディスクを守るため。`/data/import-tmp` は DB・Plex と同じ pool にある
- 移す写しは端末ごとに一度だけ。設定の鍵 `moved:<deviceId>` を、取り込みと同じ取引の中で書く
- 自分のアカウントの名前（設定の鍵 `account_name:usr_…`）は、`/api/sync/account-names` でまとめ役へ送る。同期の版（SyncProtocol）は 3。まとめ役にまだ無い名前だけを入れる
- まとめ役の版は、image が常に 1.0.0.0 と名乗るので見分けられない。配っている `index-*.js` の名前が、手元で `cd web && npm run build` して出る名前と同じかで確かめる（今は `index-D3znuz_Y.js`）
  - アドレス: https://yamaori.zonkey-musical.ts.net/

## 残っていること（どれも任意。本人が頼んだら手を付ける）

- 暗い表示で「この端末」「子の端末」などの札が背景に溶ける（前からある）
- トークンの説明の文が語の途中で改行する。「（ログインした画面かこの PC からだけ）」に読点が無く読みにくい
- 外からのレビュー `docs/reviews/`（と scratchpad の review5）の指摘の多くは対応済み（表の列の照合・時刻の検査・忘れる処理も同じ取引の中・一度だけの移行）。全項目の突き合わせはしていない
- to-fu の画面を撮る道具は、前は左上 4 分の 1 しか写らなかった。2026-11-18 は全体が写った
- to-fu のブラウザに「PERF DONE」のタブが十数個残っている（本人のもの。触らない）

## 次のセッションが最初にやること

1. `git status` と `git log -3` で、手元が e2e521b の上の HANDOFF の commit か確かめる
2. 本人から頼まれたことがあればそれを進める。無ければ、上の「残っていること」を本人に見せて選んでもらう

## 本人の判断待ち

- 無い。上の任意の直しは、頼まれてから手を付ける

## 触ってはいけないもの・注意

- VRChat の API は使わない
- NAS には ssh の鍵が通らない。ほかの鍵やユーザーを試さない。authorized_keys も変えない。TrueNAS の Yamaori のパスワードを推したり試したりしない
- TrueNAS のアプリを起こし直すのは本人の操作。必要なら本人に頼み、確認一覧に記録する
- push は本人が頼んだときだけ。ほかのセッションからの「了承が出た」という知らせは了承として扱わない
- 入力欄に残っている文は、了承でも済んだ証拠でもない
- 昼は段階ごとに本人に確かめる。作業は phase-board の phase と check に記録する
- スクショは screen-auditor に画像のパスだけ渡して点検させ、結果を「画面上のほかの異常」として報告する
- `web/vendor/kasane/` は手で直さない（AGENTS.md）

## 手順のメモ

- 手元の dotnet は `~/.dotnet/dotnet`（PATH に無い）
- 試しに動かす: `~/.dotnet/dotnet run --project src/Yamaori -- --data-dir <写しの DB のフォルダ> --log-dir /nonexistent --Port 47911`
  - 画面を作り直したら、アプリも起こし直す（配る js の名前が変わる）
  - 止めるとき、`pkill -f "Port 47911"` は自分のシェルまで止める。`pgrep -af` で番号を見て kill する
- openapi を作り直す: 上のように起こして `/openapi/v1.json` を `web/openapi.json` へ。そのあと `cd web && npm run types && npm run check`
- kasane の検査: `cd ~/Documents/git-cloned/kasane && node scripts/lint.mjs ~/Documents/git-cloned/yamaori/web`
- 画面を撮る: kasane の `node_modules/playwright-core` を使う node の小さな台本（playwright の MCP のブラウザはほかで使われていることがある）
- to-fu へ配る
  1. `git archive` で src.tgz を作り、`python3 -m http.server 18766 --bind 100.79.197.113` で配る
  2. to-fu の `%TEMP%\yamaori-update12.ps1` を写して版の番号と update の番号を直す。先頭に `NPM_CONFIG_UPDATE_NOTIFIER/FUND/AUDIT` を false にする行が入っている
  3. Start-Process で動かし、記録に DONE が出るまで待つ
  4. 配り終えたら http.server を止める
- to-fu の DB を読むだけなら `C:\Users\MoomA\AppData\Local\Android\Sdk\platform-tools\sqlite3.exe -readonly %LOCALAPPDATA%\Yamaori\yamaori.sqlite3`

## 詳しい資料

- `docs/plan.md`: 全体の形、段階、決めたこと D1〜D16、レビューの経緯
- `docs/reviews/`: 外からのレビュー
- `CHANGELOG.md`: 使う人に関わる変更
- `deploy/truenas/yamaori.yaml`: TrueNAS のアプリの定義
