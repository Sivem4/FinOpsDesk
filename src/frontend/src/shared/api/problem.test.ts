import { describe, expect, it } from 'vitest';

import { toApiError } from './problem';

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
  });
});
