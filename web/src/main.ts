import { mount } from 'svelte'
import './app.css'
import App from './App.svelte'

const app = mount(App, { target: document.getElementById('app')! })

export default app

// ホーム画面に置けるように（スマホの共有の行き先にもなる）。localhost か https のときだけ動く
if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => {})
