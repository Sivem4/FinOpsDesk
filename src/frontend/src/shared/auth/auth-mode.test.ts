import { afterEach, describe, expect, it, vi } from 'vitest';

import { browserAuthMode } from './auth-mode';

const env = import.meta.env as Record<string, string | boolean | undefined>;
const originalDev = env.DEV;
const originalMode = env.VITE_AUTH_MODE;

afterEach(() => {
  vi.unstubAllEnvs();
  env.DEV = originalDev;
  env.VITE_AUTH_MODE = originalMode;
});

describe('browser auth mode', () => {
  it('uses the development token path unless cookie mode is selected', () => {
    vi.stubEnv('VITE_AUTH_MODE', '');
    expect(browserAuthMode()).toBe('jwt');
  });

  it('uses the cookie session when development selects cookie mode', () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    expect(browserAuthMode()).toBe('cookie');
  });

  it('keeps a production build on the cookie session even if a mode variable says jwt', () => {
    env.DEV = false;
    env.VITE_AUTH_MODE = 'jwt';
    expect(browserAuthMode()).toBe('cookie');
  });
});
