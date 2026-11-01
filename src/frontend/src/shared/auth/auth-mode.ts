const cookieMode = 'cookie';

export type BrowserAuthMode = 'jwt' | 'cookie';

// Read at call time. A production build has DEV false on the env object, so a
// VITE_ variable cannot select the development token path.
export function browserAuthMode(): BrowserAuthMode {
  if (!isDevelopmentBuild()) {
    return 'cookie';
  }

  return readEnv('VITE_AUTH_MODE') === cookieMode ? 'cookie' : 'jwt';
}

function isDevelopmentBuild(): boolean {
  const dev = readEnv('DEV');
  return dev === true || dev === 'true';
}

function readEnv(name: string): string | boolean | undefined {
  return (import.meta.env as Record<string, string | boolean | undefined>)[name];
}
