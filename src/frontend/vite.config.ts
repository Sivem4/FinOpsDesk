import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// Ordinary `npm run dev` keeps the Development API. The E2E host sets this
// for one process so the Vite proxy can reach a disposable loopback API.
const apiTarget = process.env.FINOPSDESK_E2E_API_ORIGIN ?? 'https://localhost:7257';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true,
        secure: true,
      },
      '/health': {
        target: apiTarget,
        changeOrigin: true,
        secure: true,
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
  },
});
