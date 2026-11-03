import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../auth/auth-mode', () => ({
  browserAuthMode: () => 'cookie',
}));

import { sessionStore } from './session-store';
import { cookieRecoveryKeys, completeCookieRecovery, dismissRejectedRecovery } from './cookie-recovery';
import { prepareCookieFinancialPost, retryCookieFinancialMutation, runCookieFinancialMutation } from './cookie-financial-post';

const invoiceA = '11111111-1111-4111-8111-111111111111';
const bankA = '22222222-2222-4222-8222-222222222222';
const invoiceB = '33333333-3333-4333-8333-333333333333';
const bankB = '44444444-4444-4444-8444-444444444444';

describe('cookie recovery persistence', () => {
  afterEach(() => {
    sessionStorage.clear();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('keeps the original confirmation when a second confirmation is attempted', async () => {
    signIn();
    const fetchMock = vi.fn(() => new Promise(() => undefined));
    vi.stubGlobal('fetch', fetchMock);
    const first = prepareCookieFinancialPost('ConfirmExactMatch', body(invoiceA, bankA), {
      invoiceId: invoiceA,
      bankTransactionId: bankA,
    }, '55555555-5555-4555-8555-555555555555');
    const stored = sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch);
    const second = await runCookieFinancialMutation({
      operation: 'ConfirmExactMatch',
      requestBody: body(invoiceB, bankB),
      context: { invoiceId: invoiceB, bankTransactionId: bankB },
    });

    expect(second.kind).toBe('blocked');
    expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toBe(stored);
    expect(stored).toContain(first.idempotencyKey);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('does not replace a stored attempt when persistence fails', () => {
    signIn();
    prepareCookieFinancialPost('CreateAllocation', allocationBody(invoiceA, bankA), allocationContext(invoiceA, bankA), '66666666-6666-4666-8666-666666666666');
    const stored = sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('quota');
    });

    expect(() => prepareCookieFinancialPost('CreateAllocation', allocationBody(invoiceB, bankB), allocationContext(invoiceB, bankB))).toThrow(/not sent|already stored/i);
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toBe(stored);
  });

  it('does not send a descriptor after the organization changes', async () => {
    signIn();
    prepareCookieFinancialPost('ReverseAllocation', JSON.stringify({ reason: 'duplicate' }), reversalContext(invoiceA), '77777777-7777-4777-8777-777777777777');
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    sessionStore.replaceCookieSession({
      ...sessionStore.getSnapshot(),
      activeOrganization: { organizationId: '99999999-9999-4999-8999-999999999999', displayName: 'Other' },
    }, true);

    const result = await retryCookieFinancialMutation('ReverseAllocation');
    expect(result.kind).toBe('blocked');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('keeps a confirmed allocation when removeItem throws after the success body is valid', async () => {
    signIn();
    const original = Storage.prototype.removeItem;
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, key: string) {
      if (key === cookieRecoveryKeys.CreateAllocation) {
        throw new Error('locked');
      }

      return original.call(this, key);
    });
    vi.stubGlobal('fetch', vi.fn(async () => allocationResponse(invoiceA, bankA, 10.5, false)));
    const result = await runCookieFinancialMutation({
      operation: 'CreateAllocation',
      requestBody: allocationBody(invoiceA, bankA),
      context: allocationContext(invoiceA, bankA),
      idempotencyKey: '66666666-6666-4666-8666-666666666666',
    });

    expect(result.kind).toBe('confirmed');
    expect(result.kind === 'confirmed' ? result.cleanup : '').toBe('failed');
    expect(result.kind === 'blocked' ? result.text : result.kind === 'confirmed' ? result.text : '').not.toMatch(/not sent/i);
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain('66666666-6666-4666-8666-666666666666');
  });

  it('keeps a confirmed allocation when the completion read throws', async () => {
    signIn();
    const original = Storage.prototype.getItem;
    let armed = false;
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key: string) {
      if (armed && key === cookieRecoveryKeys.CreateAllocation) {
        armed = false;
        throw new Error('read');
      }

      return original.call(this, key);
    });
    vi.stubGlobal('fetch', vi.fn(async () => {
      armed = true;
      return allocationResponse(invoiceA, bankA, 10.5, false);
    }));
    const result = await runCookieFinancialMutation({
      operation: 'CreateAllocation',
      requestBody: allocationBody(invoiceA, bankA),
      context: allocationContext(invoiceA, bankA),
      idempotencyKey: '66666666-6666-4666-8666-666666666666',
    });

    expect(result.kind).toBe('confirmed');
    expect(result.kind === 'confirmed' ? result.text : '').not.toMatch(/not sent/i);
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain('66666666-6666-4666-8666-666666666666');
  });

  it('allows a corrected allocation after a known invalid first request', async () => {
    signIn();
    let calls = 0;
    vi.stubGlobal('fetch', vi.fn(async () => {
      calls += 1;
      if (calls === 1) {
        return problem(400, 'reconciliation.allocation.invalid_request');
      }

      return allocationResponse(invoiceB, bankB, 12.5, false);
    }));
    const first = await runCookieFinancialMutation({
      operation: 'CreateAllocation',
      requestBody: allocationBody(invoiceA, bankA),
      context: allocationContext(invoiceA, bankA),
      idempotencyKey: '66666666-6666-4666-8666-666666666666',
    });
    expect(first.kind).toBe('rejected');
    expect(calls).toBe(1);
    expect(dismissRejectedRecovery('CreateAllocation')).toBe(true);
    const second = await runCookieFinancialMutation({
      operation: 'CreateAllocation',
      requestBody: allocationBody(invoiceB, bankB).replace('10.5', '12.5'),
      context: { ...allocationContext(invoiceB, bankB), amount: '12.5' },
    });
    expect(second.kind).toBe('confirmed');
    expect(calls).toBe(2);
  });

  it('allows a corrected reversal after a known invalid first request', async () => {
    signIn();
    let calls = 0;
    vi.stubGlobal('fetch', vi.fn(async () => {
      calls += 1;
      if (calls === 1) {
        return problem(400, 'reconciliation.allocation.reversal_invalid_request');
      }

      return reversalResponse();
    }));
    const first = await runCookieFinancialMutation({
      operation: 'ReverseAllocation',
      requestBody: JSON.stringify({ reason: 'duplicate' }),
      context: reversalContext(invoiceA),
      idempotencyKey: '77777777-7777-4777-8777-777777777777',
    });
    expect(first.kind).toBe('rejected');
    expect(dismissRejectedRecovery('ReverseAllocation')).toBe(true);
    const second = await runCookieFinancialMutation({
      operation: 'ReverseAllocation',
      requestBody: JSON.stringify({ reason: 'corrected' }),
      context: { ...reversalContext(invoiceA), reason: 'corrected' },
    });
    expect(second.kind).toBe('confirmed');
    expect(calls).toBe(2);
    expect(second.kind === 'confirmed' ? second.cleanup : '').toBe('removed');
  });

  it('marks a first CSRF rejection as disposable and leaves an uncertain record alone', async () => {
    signIn();
    vi.stubGlobal('fetch', vi.fn(async () => problem(400, 'auth.csrf.invalid')));
    const rejected = await runCookieFinancialMutation({
      operation: 'ConfirmExactMatch',
      requestBody: body(invoiceA, bankA),
      context: { invoiceId: invoiceA, bankTransactionId: bankA },
      idempotencyKey: '55555555-5555-4555-8555-555555555555',
    });
    expect(rejected.kind).toBe('rejected');
    expect(dismissRejectedRecovery('ConfirmExactMatch')).toBe(true);

    vi.stubGlobal('fetch', vi.fn(async () => problem(409, 'reconciliation.invoice.allocation_exceeds_outstanding')));
    const conflict = await runCookieFinancialMutation({
      operation: 'CreateAllocation',
      requestBody: allocationBody(invoiceA, bankA),
      context: allocationContext(invoiceA, bankA),
      idempotencyKey: '66666666-6666-4666-8666-666666666666',
    });
    expect(conflict.kind).toBe('rejected');
    const stored = sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation);
    expect(stored).toContain('rejected');
    expect(dismissRejectedRecovery('CreateAllocation')).toBe(true);
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toBeNull();
  });

  it('does not erase earlier uncertainty when a later response is 403 or 409', async () => {
    signIn();
    let calls = 0;
    vi.stubGlobal('fetch', vi.fn(async () => {
      calls += 1;
      if (calls === 1) {
        throw new TypeError('network');
      }

      return problem(calls === 2 ? 403 : 409, calls === 2 ? 'auth.commit.forbidden' : 'reconciliation.concurrency_conflict');
    }));
    const first = await runCookieFinancialMutation({
      operation: 'CreateAllocation',
      requestBody: allocationBody(invoiceA, bankA),
      context: allocationContext(invoiceA, bankA),
      idempotencyKey: '66666666-6666-4666-8666-666666666666',
    });
    expect(first.kind).toBe('uncertain');
    const afterNetwork = sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation);
    const forbidden = await retryCookieFinancialMutation('CreateAllocation');
    expect(forbidden.kind).toBe('uncertain');
    expect(forbidden.kind === 'uncertain' ? forbidden.text : '').toMatch(/not rolled back/i);
    expect(forbidden.kind === 'uncertain' ? forbidden.text : '').not.toMatch(/not sent/i);
    expect(dismissRejectedRecovery('CreateAllocation')).toBe(false);
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain('66666666-6666-4666-8666-666666666666');
    const conflict = await retryCookieFinancialMutation('CreateAllocation');
    expect(conflict.kind).toBe('uncertain');
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain('uncertain');
    expect(afterNetwork).toContain('66666666-6666-4666-8666-666666666666');
    expect(calls).toBe(3);
  });

  it('does not delete another identity when cleanup or dismissal runs', () => {
    signIn();
    prepareCookieFinancialPost('ConfirmExactMatch', body(invoiceA, bankA), {
      invoiceId: invoiceA,
      bankTransactionId: bankA,
    }, '55555555-5555-4555-8555-555555555555');
    const stored = sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch);
    sessionStore.replaceCookieSession({
      ...sessionStore.getSnapshot(),
      subject: 'someone-else',
    }, true);
    const remove = vi.spyOn(Storage.prototype, 'removeItem');
    expect(dismissRejectedRecovery('ConfirmExactMatch')).toBe(false);
    expect(() => completeCookieRecovery('ConfirmExactMatch', '55555555-5555-4555-8555-555555555555')).toThrow(/could not clear/i);
    expect(remove).not.toHaveBeenCalled();
    sessionStore.replaceCookieSession({
      ...sessionStore.getSnapshot(),
      subject: 'oidc-operator',
    }, true);
    expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toBe(stored);
  });
});

function body(invoiceId: string, bankTransactionId: string): string {
  return `{"invoiceId":${JSON.stringify(invoiceId)},"bankTransactionId":${JSON.stringify(bankTransactionId)}}`;
}

function allocationBody(invoiceId: string, bankTransactionId: string): string {
  return `{"invoiceId":${JSON.stringify(invoiceId)},"bankTransactionId":${JSON.stringify(bankTransactionId)},"amount":{"amount":10.5,"currency":"PLN"}}`;
}

function allocationContext(invoiceId: string, bankTransactionId: string): Record<string, string> {
  return { invoiceId, bankTransactionId, amount: '10.5', currency: 'PLN' };
}

function reversalContext(allocationId: string): Record<string, string> {
  return {
    allocationId,
    invoiceId: '88888888-8888-4888-8888-888888888888',
    bankTransactionId: '99999999-9999-4999-8999-999999999999',
    reason: 'duplicate',
    amount: '10.5',
    currency: 'PLN',
  };
}

function allocationResponse(invoiceId: string, bankTransactionId: string, amount: number, replayed: boolean): Response {
  return new Response(JSON.stringify({
    allocationId: '77777777-7777-4777-8777-777777777777',
    invoiceId,
    bankTransactionId,
    amount: { amount, currency: 'PLN' },
    replayed,
  }), { status: 201, headers: { 'content-type': 'application/json' } });
}

function reversalResponse(): Response {
  return new Response(JSON.stringify({
    allocationId: invoiceA,
    status: 'reversed',
    amount: { amount: 10.5, currency: 'PLN' },
    reversedAt: '2026-08-24T12:00:00.0000000+00:00',
    reason: 'corrected',
    replayed: false,
  }), { status: 200, headers: { 'content-type': 'application/json' } });
}

function problem(status: number, code: string): Response {
  return new Response(JSON.stringify({ title: 'Rejected', status, code }), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });
}

function signIn(): void {
  sessionStore.replaceCookieSession({
    authenticated: true,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada',
    expiresAt: null,
    organizations: [{ organizationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', displayName: 'North' }],
    activeOrganization: { organizationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', displayName: 'North' },
    authorization: 'verified',
    capabilities: ['invoices.read', 'reconciliation.confirm', 'allocations.create', 'allocations.reverse'],
  }, true);
}
