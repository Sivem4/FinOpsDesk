import { describe, expect, it } from 'vitest';

import { readRetryAfter, toApiError } from './problem';

describe('problem details', () => {
  it('reads the code, title, and detail from a problem response', () => {
    const error = toApiError(
      400,
      JSON.stringify({
        title: 'Invalid pagination.',
        detail: 'Page must be at least 1.',
        code: 'reconciliation.invoices.invalid_pagination',
      }),
    );

    expect(error.status).toBe(400);
    expect(error.code).toBe('reconciliation.invoices.invalid_pagination');
    expect(error.title).toBe('Invalid pagination.');
    expect(error.detail).toBe('Page must be at least 1.');
  });

  it('hides exception text and classifies an empty unauthorized response', () => {
    const unauthorized = toApiError(401, '');
    const leaked = toApiError(500, JSON.stringify({ detail: 'at System.InvalidOperationException()' }));

    expect(unauthorized.title).toBe('Unauthorized');
    expect(unauthorized.detail).toBe('');
    expect(leaked.detail).toBe('');
    expect(unauthorized.retryAfterSeconds).toBeNull();
  });

  it('reads a positive delay and a future HTTP date, and ignores a bad hint', () => {
    const now = Date.UTC(2026, 10, 9, 21, 0, 0);

    expect(readRetryAfter('4', now)).toBe(4);
    expect(readRetryAfter('0004', now)).toBe(4);
    expect(readRetryAfter(null, now)).toBeNull();
    expect(readRetryAfter('soon', now)).toBeNull();
    expect(readRetryAfter('2026-11-09', now)).toBeNull();
    expect(readRetryAfter('Tue, 31 Nov 2026 00:00:00 GMT', now)).toBeNull();
    expect(readRetryAfter('9007199254740993', now)).toBeNull();
    expect(readRetryAfter('0009007199254740993', now)).toBeNull();
    expect(readRetryAfter('Mon, 09 Nov 2026 20:00:00 GMT', now)).toBeNull();
    expect(readRetryAfter('Mon, 09 Nov 2026 21:00:02 GMT', now)).toBe(2);
    const yearFifty = readRetryAfter('Saturday, 01-Jan-50 00:00:00 GMT', now);
    expect(yearFifty).toBe(readRetryAfter('Sat, 01 Jan 2050 00:00:00 GMT', now));
    expect(yearFifty).toBeGreaterThan(0);
    const beyondFiftyYears = readRetryAfter('Saturday, 01-Jan-77 00:00:00 GMT', now);
    expect(beyondFiftyYears).toBe(readRetryAfter('Sat, 01 Jan 1977 00:00:00 GMT', now));
    expect(beyondFiftyYears).toBeNull();
    const centuryBoundary = Date.UTC(2099, 11, 31, 23, 59, 58);
    expect(readRetryAfter('Friday, 01-Jan-00 00:00:00 GMT', centuryBoundary)).toBe(2);
    expect(readRetryAfter('Fri, 01 Jan 2100 00:00:00 GMT', centuryBoundary)).toBe(2);
    expect(toApiError(429, '{"title":"Too many requests.","code":"finopsdesk.rate_limit.exceeded"}', '0004').retryAfterSeconds).toBe(4);
  });
});
