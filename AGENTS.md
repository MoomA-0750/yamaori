# AGENTS.md

- Web の画面（`web/`）の UI は kasane の決まり（kasane の `DESIGN.md`）に従う。色・字・余白・角丸は kasane の名前で書き、Yamaori にしかない色は `--yamaori-*` で共通の名前から作る。
- kasane の CSS は `web/vendor/kasane/` に写してあり、`web/src/app.css` から相対パスで読む（Docker・CI では kasane が無いため）。手で直さない。写し直すのは kasane のフォルダで `node scripts/kasane-sync.mjs <yamaori のフォルダ>`（何を写すかは根の `kasane-native.json`）。古くないかは `--check`。
- 検査は kasane のフォルダで `node scripts/lint.mjs <yamaori のフォルダ>/web`（`vendor/` は見ない。許すものは `web/kasane-allow.txt`）。
