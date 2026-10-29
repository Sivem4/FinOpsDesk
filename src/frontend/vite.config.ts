import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

const apiTarget = 'https://localhost:7257';

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
