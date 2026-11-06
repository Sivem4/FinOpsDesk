import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterAll, afterEach, beforeAll } from 'vitest';

import { resetReversalAttemptStateForTests } from '../features/allocations/reversal-attempt';
import { resetReviewCaseCommentAttemptStateForTests } from '../features/review-cases/review-case-comment-attempt';
import { resetAllocationAttemptStateForTests } from '../features/reconciliation/allocation-attempt';
import { resetFinancialStatusForTests } from '../shared/api/financial-status';
import { queryClient } from '../shared/api/query-client';
import { sessionStore } from '../shared/api/session-store';
import { resetSessionReadsForTests } from '../shared/auth/browser-session';
import { resetCookieActionsForTests } from '../shared/auth/cookie-actions';
import { resetSessionCoordinationForTests } from '../shared/auth/session-coordination';
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
  resetSessionReadsForTests();
  resetCookieActionsForTests();
  resetSessionCoordinationForTests();
  queryClient.clear();
  resetFinancialStatusForTests();
  localStorage.clear();
  sessionStorage.clear();
  resetAllocationAttemptStateForTests();
  resetReversalAttemptStateForTests();
  resetReviewCaseCommentAttemptStateForTests();
});

afterAll(() => {
  server.close();
});
