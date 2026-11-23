# 引き継ぎ（今の状態）

最終更新: 2026-11-23（夜）

## 目的

VRChat の記録を残す Yamaori を、TrueNAS を「まとめ役」（記録を全部持ち WebUI を配る）、Windows の to-fu を「メンバー」（記録を送る）にした端末のグループで使えるようにする。今は VRCX との連携を深めた版を本番へ出している途中。

## 今どこまでできたか

- VRCX との連携を深めた（調べた結果・決めたこと D17・やらないこと・実データでの確認は `docs/vrcx-integration.md`）
  - 送る写しは取り込む表だけ（cookies・ログイン情報・フィードは送らない）。取り込む中身の指紋が変わったときだけ送る
  - 入れたばかりの VRCX でもアカウントが分かり、遊び始めても「別の DB」で止まらない
  - VRChat のノートを人のメモとして入れる（出どころ vrchat-note。VRCX のメモとは別の行）。新しい通知の表（_notifications_v2）も読む
  - まとめ役はメンバーごとの取り込み結果を `GET /api/sync/import/vrcx/result` で返し、メンバーは設定の「前の結果」に出す（古いまとめ役では「分からない」）
  - 取り込み（VAT・VRCX）のあとに同じ端末・同じ時間のログが届いたら、取り込み側の訪問を外す・切る（ログを正。`ImportGuard.TrimImportedToLog`）
  - `yamaori vrcx-snapshot --out <file>` を足した
  - 試験 197 本通る。change-reviewer の指摘 5 件は直した
- git: kasane-trial は origin に push 済み（この HANDOFF の commit まで）。origin の main は 6df5646（別のセッションの kasane 1.3.1 の CSS）。kasane-trial はそれを merge してあり、main へ早送りで push できる
- tofu: Yamaori 0.1.11（7dac569 の源）を入れた（2026-11-23 18:32）。18:35 に絞った写しを本番のまとめ役へ送り、結果は「まとめ役の版が古く、取り込みの結果は分からない」（想定どおり）。ログイン情報入りの丸ごとの写しを送るのは止まった。訪問 7735 件は更新の前後で同じ
- 本番のまとめ役（TrueNAS）: まだ e2e521b の版。古い版でも tofu の新しい写しは読める（確かめ済み）が、アカウントが分からずフレンドの一覧と VRChat のノートは入らない
- 裏で動かしているもの: 無い（試しのまとめ役・メンバー・配る口は止めて消した）

## 残っていること

- 本番のまとめ役を新しい版にする（本人の了承待ち。下）
  1. `git push origin kasane-trial:main`（早送り）。6df5646 の CSS の変更も一緒に本番に出る
  2. CI の image（ghcr.io/mooma-0750/yamaori:latest）ができるのを待つ
  3. 本人に TrueNAS のアプリの起こし直しを頼む（確認一覧に記録）
  4. 確かめる: 配っている `index-*.js` の名前が、手元で `cd web && npm run build` して出る名前と同じか／tofu の設定の取り込みの「前の結果」（DB の `import_last:vrcx`）が次の送信のあと「まとめ役で取り込んだ」になるか（VRCX の中身が変わったとき送る。最短は間隔の 60 分）
- 任意（本人が頼んだら）
  - 画面でメモの出どころ（vrchat-note）の札を出す
  - VRCX の役を持つ端末が 2 台以上になると、出どころ "vrcx" と origin が 1 つなのでぶつかる（今は役を 1 台に限っている）
  - 出た時刻が推定の訪問で、会った人の出た時刻が入った時刻より数秒前になることがある（前から。VRCX の取り込み）
  - 暗い表示で「この端末」「子の端末」などの札が背景に溶ける／トークンの説明の文が語の途中で改行する
  - 外からのレビュー `docs/reviews/` の全項目の突き合わせはしていない

## 次のセッションが最初にやること

1. `git status` と `git log -3` で、手元が origin/kasane-trial と同じか確かめる
2. 確認一覧（phase-board の check）の Yamaori の項目を見て、本人の返事があればそれを進める。main の push の了承が出ていれば「残っていること」の 1〜4 をする

## 本人の判断待ち（確認一覧にも積んである）

- main へ push してよいか（本番のまとめ役へ出すため。了承は「kasane-trial の push」だったので、main は止められた）
- VRCX の連携の仮決定（フィードは入れない・ノートは別の出どころ・差分同期はしない など）でよいか
- VRCX のログイン情報が 0.1.10 のころまとめ役へ送られていた件で、VRCX にログインし直すか

## 決まったこと（会話にしか無かったもの）

- 受け取る量の上限: まとめ役へ移す写し（move）1 GiB（`ImportJobs.MaxMoveUpload`）、VAT・VRCX 8 GiB、普段の同期は Kestrel の既定の約 30 MB（D13）。上限はまとめ役のディスクを守るため（`/data/import-tmp` は DB・Plex と同じ pool）
- 移す写しは端末ごとに一度だけ（設定の鍵 `moved:<deviceId>` を取り込みと同じ取引で書く）
- 自分のアカウントの名前は `/api/sync/account-names` でまとめ役へ送る。同期の版（SyncProtocol）は 3（今回も上げていない）
- まとめ役の版は image が常に 1.0.0.0 と名乗るので、配っている `index-*.js` の名前で見分ける（e2e521b は `index-D3znuz_Y.js`。新しい版は作り直して確かめる）。アドレス: https://yamaori.zonkey-musical.ts.net/
- 戻し方
  - tofu: `%USERPROFILE%\yamaori-rollback\Yamaori-Setup-0.1.10.exe` を入れ直す（DB の表の版は変えていないので DB はそのまま）
  - 本番: TrueNAS のアプリの image を前の digest に戻して起こし直す（DB はそのまま）
- 試して分かったこと
  - Yamaori.exe は窓のアプリなので、PowerShell からコマンドとして使うときは `Start-Process -Wait` で待つ。`-RedirectStandardOutput` 付きで常駐させると to-fu の run_command が返らなくなる
  - to-fu の run_command は 120 秒で裏に回り、300 秒何も出ないと切れる。待つのは 1 回 250 秒まで
  - 引用の入れ子が崩れるので、台本は to-fu にファイルで書いて `powershell -File` で動かす
  - メンバーはグループに入ったあと起こし直さないと役を読まない（restartRequired）。役は同期の間隔（既定 10 分）で読み直す
  - 自分のシェルから `pkill -f "http.server 18766"` すると自分も止まる（exit 144）。`kill $(pgrep -f … | head -1)` を使う
  - image は main への push（か v* のタグ）でだけ作られる

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
  2. to-fu の `%TEMP%\yamaori-update13.ps1`（0.1.11 で使った）を写して版の番号と update の番号を直す（次は 0.1.12・update14）。先頭に `NPM_CONFIG_UPDATE_NOTIFIER/FUND/AUDIT` を false にする行が入っている
  3. Start-Process で動かし、記録に DONE が出るまで待つ
  4. 配り終えたら http.server を止める
- to-fu の DB を読むだけなら `C:\Users\MoomA\AppData\Local\Android\Sdk\platform-tools\sqlite3.exe -readonly %LOCALAPPDATA%\Yamaori\yamaori.sqlite3`

## 詳しい資料

- `docs/vrcx-integration.md`: VRCX 連携の調べた結果・決めたこと・実データでの確認
- `docs/plan.md`: 全体の形、段階、決めたこと D1〜D17、レビューの経緯
- `docs/reviews/`: 外からのレビュー
- `CHANGELOG.md`: 使う人に関わる変更
- `deploy/truenas/yamaori.yaml`: TrueNAS のアプリの定義
