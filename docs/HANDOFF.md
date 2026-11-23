# 引き継ぎ（今の状態）

最終更新: 2026-11-23（夜）

## 目的

VRChat の記録を残す Yamaori を、TrueNAS を「まとめ役」（記録を全部持ち WebUI を配る）、Windows の to-fu を「メンバー」（記録を送る）にした端末のグループで使えるようにする。

## 今の作業（2026-11-23）: VRCX との連携を深める — tofu は反映済み、本番のまとめ役は main の push の了承待ち

- 反映（本人の了承あり）
  - kasane-trial を 7dac569 まで push 済み（別のセッションが main に push した 6df5646 = kasane 1.3.1 の CSS を merge してある）。change-reviewer の指摘 5 件は 4d432ac で直した
  - tofu を 0.1.11 にした（18:32）。18:35 に絞った写しを本番のまとめ役へ送り、結果は「まとめ役の版が古く、分からない」（想定どおり）。ログイン情報入りの丸ごとの写しを送るのは止まった
  - tofu の戻し方: `%USERPROFILE%\yamaori-rollback\Yamaori-Setup-0.1.10.exe` を入れ直す（DB の表の版は変えていないので DB はそのまま）
  - 本番のまとめ役: image は main への push でだけ作られる。了承は「kasane-trial の push」だったので、main への push は止められた。本人に `kasane-trial:main` の push の了承をもらう → CI の image → 本人が TrueNAS のアプリを起こし直す → 配っている js の名前と、tofu の import_last:vrcx が「まとめ役で取り込んだ」になるかで確かめる
  - 本番の戻し方: TrueNAS のアプリの image を前の digest に戻して起こし直す（DB の表の版は同じ）


- 調べた結果・決めたこと（D17）・やらないことは `docs/vrcx-integration.md`
- 実装は f06444b・c20d4e5・85f08a4。変えたのは VrcxSnapshot.cs（新）・VrcxImport.cs・ImportGuard.cs・ImportJobs.cs（VrcxWorker）・ImportSettings.cs・Program.cs（`vrcx-snapshot`）
- tofu の実 VRCX（2026.11.14、DB 版 18、入れたばかり）で確かめたこと（tofu の中に別のデータフォルダでまとめ役とメンバーを立てた。入っている 0.1.10 と本番のまとめ役には触れていない。試しのフォルダは消した）
  - 写しは 516 KB → 111 KB。cookies・ログイン情報・フィードは入らない（元の DB にある `savedcredentials` の文字が写しに無い）
  - 一人で取り込み: アカウント 1・フレンドの一覧 40・VRChat のノート 9。2 回目でも増えない
  - グループ: メンバーが送る → まとめ役が取り込む → メンバーが結果を読み「まとめ役で取り込んだ」と残す。指紋は一人のときと同じ
  - VRCX がフィードで DB を書いても、指紋が同じなので送らない
  - 手元で、古いまとめ役（e2e521b）の取り込みが新しい写しを読めることも確かめた（作り物の見本で）
- 2026-11-23 17:38 に本人が tofu で一度遊んだあとの実データでも確かめた（`docs/vrcx-integration.md` の最後）。VRCX のあとにログが届くと二重になる問題を見つけて直した（c20d4e5）。起こし直すと結果を読まない穴も直した（85f08a4）。試験 197 本通る。試しのフォルダは消した
- まだしていない
  - 本番のまとめ役（TrueNAS）と tofu の Yamaori の更新（本人の判断待ち。下）
  - 画面でノートの出どころの札を出す（任意）
- tofu の Yamaori 0.1.10 は、今も 60 分ごとに VRCX の DB を丸ごと（cookies・ログイン情報入り）まとめ役へ送っている。新しい版を tofu に入れると止まる

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

1. `git status` と `git log -5` で、手元が kasane-trial の最新（VRCX の連携の commit を含む）か確かめる。push はまだしていない
2. 本人から頼まれたことがあればそれを進める。無ければ、上の「残っていること」を本人に見せて選んでもらう

## 本人の判断待ち（確認一覧にも積んである）

- VRCX の連携の仮決定（フィードは入れない・ノートは別の出どころ・差分同期はしない など）でよいか
- 新しい版を tofu に入れるか（入れると、ログイン情報入りの写しを送るのが止まる。本番のまとめ役が古いままでも動き、結果は「分からない」と出る）
- 本番のまとめ役（TrueNAS）を新しい版にするか（push と image が要る。起こし直しは本人の操作）。tofu で VRChat を遊び始める前に更新しないと、古いまとめ役ではアカウントが分からず、遊んだあと一度「入れ替え」が要る場合がある
- VRCX のログイン情報がまとめ役へ送られていた件で、VRCX の再ログインなどをするか

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
