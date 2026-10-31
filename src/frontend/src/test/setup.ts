import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterAll, afterEach, beforeAll } from 'vitest';

import { resetReversalAttemptStateForTests } from '../features/allocations/reversal-attempt';
import { resetAllocationAttemptStateForTests } from '../features/reconciliation/allocation-attempt';
import { queryClient } from '../shared/api/query-client';
import { sessionStore } from '../shared/api/session-store';
import { installMultipartFormDataCompat } from './form-data-compat';
import { server } from './server';

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
  installMultipartFormDataCompat();
});

afterEach(() => {
  cleanup();
  server.resetHandlers();
  sessionStore.resetForTests();
  queryClient.clear();
  localStorage.clear();
  sessionStorage.clear();
  resetAllocationAttemptStateForTests();
  resetReversalAttemptStateForTests();
});

afterAll(() => {
  server.close();
});
