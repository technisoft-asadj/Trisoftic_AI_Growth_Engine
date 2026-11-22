import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The API runs on :5232 in development; the browser only ever talks to the Vite origin so the session cookie stays same-site.
export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/api': 'http://localhost:5232', '/health': 'http://localhost:5232', '/r': 'http://localhost:5232' } },
})
