# Yamaori を親（サーバー）として動かすコンテナ。子の端末から記録を受け取り、WebUI・API を配る。
# TrueNAS のアプリとして動かすときは deploy/truenas/yamaori.yaml。設定は環境変数 YAMAORI_<項目> でも決められる。
# VRChat のログはないので、ログの見張りは何もしない。写真は NAS をつないだフォルダを /photos に渡せば目録に入る。
#
#   docker build -t yamaori .
#   docker run -d --name yamaori -p 47800:47800 -e TZ=Asia/Tokyo -v yamaori-data:/data -v /mnt/nas/VRChat:/photos:ro yamaori
#   （TZ は日の区切りに使う。付けないと UTC で区切る）
#   docker exec -it yamaori yamaori set-password --data-dir /data     # 最初にパスワードを決める
#
FROM node:22-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json web/.npmrc ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npx vite build --outDir /out/wwwroot --emptyOutDir

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Yamaori.slnx ./
COPY src/ src/
RUN dotnet publish src/Yamaori -c Release -r linux-x64 --self-contained false -o /out/app
COPY --from=web /out/wwwroot /out/app/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/app ./
RUN ln -s /app/Yamaori /usr/local/bin/yamaori && mkdir -p /data /photos
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
# 設定のファイルの見張りを切る（コンテナでは要らず、NAS の上でファイルの見張りの数を使い切らないように）
ENV DOTNET_hostBuilder__reloadConfigOnChange=false
# 家の中から見せる（パスワードかトークンが要る）。データは /data に
EXPOSE 47800
VOLUME ["/data"]
ENTRYPOINT ["/app/Yamaori"]
CMD ["--data-dir", "/data", "--log-dir", "/nonexistent", "--Listen", "lan", "--PhotoDirs:0", "/photos", "--DeviceName", "server"]
