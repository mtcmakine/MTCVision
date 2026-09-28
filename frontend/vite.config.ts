import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    host: true,
  },
  build: {
    // Built straight into the backend, which serves it. One install, one service, one URL — rather
    // than a server plus a dev server the operator has to remember to start as well.
    outDir: '../backend/wwwroot',
    emptyOutDir: true,
  },
})
