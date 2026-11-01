import { lazy, Suspense, type ReactNode } from 'react';

import { browserAuthMode } from './auth-mode';
import { CookieAuthGate } from './CookieAuthGate';

const DevAuthGate = import.meta.env.DEV ? lazy(() => import('./DevAuthGate')) : null;

export function AuthGate({ children }: { children: ReactNode }) {
  if (browserAuthMode() === 'jwt' && DevAuthGate !== null) {
    return (
      <Suspense fallback={null}>
        <DevAuthGate>{children}</DevAuthGate>
      </Suspense>
    );
  }

  return <CookieAuthGate />;
}
