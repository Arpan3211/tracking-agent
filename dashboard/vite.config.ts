import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Proxy /api and /api/v1/ws to the local backend so the browser sees the
// dashboard and API as the SAME origin during dev - this matters because
// auth uses httpOnly cookies with SameSite=Lax; a cross-origin dev setup
// (5173 -> 8000 directly) would need the cookies to be cross-site instead,
// complicating the CSRF double-submit story for no real benefit locally.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:8000',
        changeOrigin: true,
        ws: true,
      },
    },
  },
})
