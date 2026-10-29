import { lazy, Suspense, type ReactNode } from 'react';

import { ProductionAuthNotice } from './ProductionAuthNotice';

const DevAuthGate = import.meta.env.DEV ? lazy(() => import('./DevAuthGate')) : null;

export function AuthGate({ children }: { children: ReactNode }) {
  if (!import.meta.env.DEV || DevAuthGate === null) {
    return <ProductionAuthNotice />;
  }

  return (
    <Suspense fallback={null}>
      <DevAuthGate>{children}</DevAuthGate>
    </Suspense>
  );
}
