import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterAll, afterEach, beforeAll } from 'vitest';

import { queryClient } from '../shared/api/query-client';
import { sessionStore } from '../shared/api/session-store';
import { server } from './server';

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
});

afterEach(() => {
  cleanup();
  server.resetHandlers();
  sessionStore.resetForTests();
  queryClient.clear();
  localStorage.clear();
  sessionStorage.clear();
});

afterAll(() => {
  server.close();
});
