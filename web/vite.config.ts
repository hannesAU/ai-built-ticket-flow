import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Dev: run the .NET server (port 5199), then `npm run dev` and open
// http://localhost:5173/auth?t=<token printed by the server>.
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/TicketFlow.Server/wwwroot', emptyOutDir: true },
  server: {
    proxy: {
      '/api': { target: 'http://127.0.0.1:5199' },
      '/auth': { target: 'http://127.0.0.1:5199' },
    },
  },
})
