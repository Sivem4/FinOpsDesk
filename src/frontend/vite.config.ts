import react from '@vitejs/plugin-react';
import type { ProxyOptions } from 'vite';
import { loadEnv } from 'vite';
import { defineConfig } from 'vitest/config';

// Ordinary `npm run dev` keeps the Development API. The E2E host sets this
// for one process so the Vite proxy can reach a disposable loopback API.
const apiTarget = process.env.FINOPSDESK_E2E_API_ORIGIN ?? 'https://localhost:7257';

function apiProxy(): ProxyOptions {
  return {
    target: apiTarget,
    changeOrigin: true,
    secure: true,
  };
}

function browserAuthProxy(): ProxyOptions {
  return {
    target: apiTarget,
    changeOrigin: true,
    secure: true,
    configure(proxy) {
      proxy.on('proxyRes', (proxyRes) => {
        const location = proxyRes.headers.location;

        if (typeof location === 'string' && location.startsWith(apiTarget)) {
          const relative = location.slice(apiTarget.length);
          proxyRes.headers.location = relative.length === 0 ? '/' : relative;
        }
      });
    },
  };
}

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const cookieMode = process.env.VITE_AUTH_MODE === 'cookie' || env.VITE_AUTH_MODE === 'cookie';
  const proxy: Record<string, ProxyOptions> = {
    '/api': apiProxy(),
    '/health': apiProxy(),
  };

  if (cookieMode) {
    proxy['/login'] = browserAuthProxy();
    proxy['/logout'] = browserAuthProxy();
    proxy['/signin-oidc'] = browserAuthProxy();
  }

  return {
    plugins: [react()],
    server: {
      proxy,
    },
    test: {
      environment: 'jsdom',
      setupFiles: './src/test/setup.ts',
      maxWorkers: 2,
    },
  };
});
