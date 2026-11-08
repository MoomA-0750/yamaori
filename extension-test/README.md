# 拡張機能の試し

拡張機能（`extension/`）を入れた Chromium で、偽の vrchat.com を相手に 3 つの使い方を確かめる。
ブラウザの名前解決で `vrchat.com` を手元の HTTPS（127.0.0.1:18443、自己署名）へ向け、ほかの名前は全部見つからないことにするので、本物の vrchat.com や外へは一切つながらない。

1. 既定: ワールドのページのボタンを押すと、ワールドの情報が Yamaori に入る
2. フレンドの一覧を自動で記録: 一覧が変わると、増えた・減った人が記録される
3. まとめて取る: 情報のないワールドを裏のタブで開いて取り、閉じる

```sh
# 手元で Yamaori を起こし（例 --port 47813）、情報のないワールドを 1 つ入れておき、トークンを作る
cd extension-test
openssl req -x509 -newkey rsa:2048 -nodes -keyout key.pem -out cert.pem -days 2 -subj "/CN=vrchat.com" -addext "subjectAltName=DNS:vrchat.com"
CHROME=<chromium のパス> PW=<playwright-core のパス> EXT=$(realpath ../extension) Y=http://localhost:47813 TOKEN=<トークン> PROFILE=/tmp/yamaori-ext-profile SHOT=/tmp/ext.png node e2e.mjs
```
