import { svelte } from '@sveltejs/vite-plugin-svelte'
import { defineConfig } from 'vite'

// ビルドした画面は、Yamaori が配るフォルダ（src/Yamaori/wwwroot）へ出す。
// 開発中（npm run dev）は、/api を動いている Yamaori（localhost:47800）へ回す。
export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: '../src/Yamaori/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': { target: 'http://localhost:47800', changeOrigin: false },
    },
  },
})
