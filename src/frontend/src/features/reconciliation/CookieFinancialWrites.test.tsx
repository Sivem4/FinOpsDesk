import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../../shared/auth/auth-mode', () => ({
  browserAuthMode: () => 'cookie',
}));

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { prepareCookieFinancialPost } from '../../shared/api/cookie-financial-post';
import { cookieRecoveryKeys } from '../../shared/api/cookie-recovery';
import { readMoneyAmount, type DecimalString } from '../../shared/money/money';
import { explicitAmountAttemptStorageKey } from './allocation-attempt';
import { server } from '../../test/server';
import {
  CookieAllocationForm,
  CookieConfirmationDialog,
  CookieReversalControl,
  CookieReversalRecovery,
} from './CookieFinancialWrites';

const invoiceA = '11111111-1111-4111-8111-111111111111';
const invoiceB = '33333333-3333-4333-8333-333333333333';
const bankA = '22222222-2222-4222-8222-222222222222';
const bankB = '44444444-4444-4444-8444-444444444444';
const allocationA = '55555555-5555-4555-8555-555555555555';
const allocationB = '66666666-6666-4666-8666-666666666666';
const createdAllocation = '77777777-7777-4777-8777-777777777777';

describe('cookie financial writes', () => {
  it('blocks a second confirmation and keeps the original key', async () => {
    signIn();
    let calls = 0;
    server.use(http.post('/api/matches/confirm', () => {
      calls += 1;
      return new Promise(() => undefined);
    }));
    renderDialog(pair(invoiceA, bankA));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    await waitFor(() => expect(calls).toBe(1));
    const stored = sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch);
    renderDialog(pair(invoiceB, bankB));
    const dialogs = screen.getAllByRole('dialog');
    expect(within(dialogs[dialogs.length - 1]).getByRole('button', { name: 'Confirm allocation' })).toBeDisabled();
    expect(screen.getAllByRole('button', { name: 'Send the same request again', hidden: true }).length).toBeGreaterThan(0);
    expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toBe(stored);
    expect(calls).toBe(1);
  });

  it('blocks a second allocation and a second reversal without a new key', async () => {
    signIn();
    server.use(http.post('/api/allocations', () => new Promise(() => undefined)));
    renderAllocation();
    fireEvent.change(screen.getByLabelText('Invoice'), { target: { value: invoiceA } });
    fireEvent.change(screen.getByLabelText('Bank transaction'), { target: { value: bankA } });
    fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '10.50' } });
    fireEvent.click(screen.getByRole('button', { name: 'Allocate an amount' }));
    await waitFor(() => expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain(invoiceA));
    const allocation = sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation);
    fireEvent.change(screen.getByLabelText('Invoice'), { target: { value: invoiceB } });
    fireEvent.click(screen.getByRole('button', { name: 'Allocate an amount' }));
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toBe(allocation);

    server.use(http.post('/api/allocations/:id/reverse', () => new Promise(() => undefined)));
    renderReversal(allocationA);
    fireEvent.click(screen.getByRole('button', { name: 'Reverse allocation' }));
    const dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Reason'), { target: { value: 'duplicate receipt' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reverse allocation' }));
    await waitFor(() => expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).toContain(allocationA));
    const reversal = sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation);
    renderReversal(allocationB);
    fireEvent.click(screen.getAllByRole('button', { name: 'Reverse allocation' }).at(-1)!);
    expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).toBe(reversal);
  });

  it('sends the stored reversal after a lost response and ignores a tampered allocation id', async () => {
    signIn();
    const seen: Array<{ url: string; key: string | null; authorization: string | null; organization: string | null; csrf: string | null }> = [];
    server.use(http.post('/api/allocations/:id/reverse', ({ request }) => {
      seen.push({
        url: request.url,
        key: request.headers.get('Idempotency-Key'),
        authorization: request.headers.get('Authorization'),
        organization: request.headers.get('X-Organization-Id'),
        csrf: request.headers.get('X-CSRF-Token'),
      });
      return HttpResponse.json({ replayed: false });
    }));
    prepareCookieFinancialPost('ReverseAllocation', JSON.stringify({ reason: 'duplicate receipt' }), {
      allocationId: allocationA,
      invoiceId: invoiceA,
      bankTransactionId: bankA,
      reason: 'duplicate receipt',
      amount: '10.5',
      currency: 'PLN',
    }, '88888888-8888-4888-8888-888888888888');
    renderRecovery();
    expect(await screen.findByRole('button', { name: 'Send the same request again' })).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Send the same request again' }));
    await waitFor(() => expect(seen).toHaveLength(1));
    expect(seen[0]?.authorization).toBeNull();
    expect(seen[0]?.organization).toBeNull();
    expect(seen[0]?.csrf).toBe('csrf-session');
    expect(seen[0]?.url).toContain(allocationA);
    expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).not.toBeNull();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();

    const tampered = JSON.parse(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation) ?? '{}') as { context: { allocationId: string } };
    tampered.context.allocationId = allocationB;
    sessionStorage.setItem(cookieRecoveryKeys.ReverseAllocation, JSON.stringify(tampered));
    sessionStore.replaceCookieSession(sessionStore.getSnapshot(), false);
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Send the same request again' })).not.toBeInTheDocument());
    expect(seen).toHaveLength(1);
  });

  it('keeps an uncertain descriptor when the body arrives after an organization switch', async () => {
    signIn();
    let release: ((bytes: Uint8Array) => void) | undefined;
    server.use(http.post('/api/matches/confirm', () => new HttpResponse(new ReadableStream({
      start(controller) {
        release = (bytes) => {
          controller.enqueue(bytes);
          controller.close();
        };
      },
    }), { status: 201, headers: { 'Content-Type': 'application/json' } })));
    queryClient.setQueryData(['matches', 99, 0], { foreign: true });
    renderDialog(pair(invoiceA, bankA));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    await waitFor(() => expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toContain(invoiceA));
    const stored = sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch);
    sessionStore.replaceCookieSession({
      ...cookieSession(),
      subject: 'other-subject',
    }, true);
    const current = sessionStore.getSnapshot();
    queryClient.setQueryData(['matches', current.epoch, current.readiness], { foreign: true });
    release?.(new TextEncoder().encode(JSON.stringify(successBody(false))));
    await waitFor(() => expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument());
    expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toBe(stored);
    expect(queryClient.getQueryData(['matches', current.epoch, current.readiness])).toEqual({ foreign: true });
  });

  it('does not confirm a malformed success body or an HTTP 500', async () => {
    signIn();
    server.use(http.post('/api/matches/confirm', () => HttpResponse.json({ replayed: false }, { status: 201 })));
    renderDialog(pair(invoiceA, bankA));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    expect(await screen.findByText(/outcome is uncertain/i)).toBeInTheDocument();
    expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toContain(invoiceA);
    cleanup();
    sessionStorage.clear();

    server.use(http.post('/api/allocations', () => new HttpResponse('unavailable', { status: 500 })));
    renderAllocation();
    fireEvent.change(screen.getByLabelText('Invoice'), { target: { value: invoiceA } });
    fireEvent.change(screen.getByLabelText('Bank transaction'), { target: { value: bankA } });
    fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '10.50' } });
    fireEvent.click(screen.getByRole('button', { name: 'Allocate an amount' }));
    expect((await screen.findAllByText(/does not show that the server rolled/i)).length).toBeGreaterThan(0);
    expect(screen.queryByText('This attempt was rejected. The original idempotency key was not replaced.')).not.toBeInTheDocument();
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain(invoiceA);
  });

  it('hides recovery after role revocation and keeps the original body', async () => {
    signIn();
    const record = prepareCookieFinancialPost('ReverseAllocation', JSON.stringify({ reason: 'duplicate receipt' }), {
      allocationId: allocationA,
      invoiceId: invoiceA,
      bankTransactionId: bankA,
      reason: 'duplicate receipt',
      amount: '10.5',
      currency: 'PLN',
    }, '88888888-8888-4888-8888-888888888888');
    renderRecovery();
    expect(await screen.findByRole('button', { name: 'Send the same request again' })).toBeVisible();
    sessionStore.replaceCookieSession({
      ...cookieSession(),
      capabilities: ['allocations.read'],
    }, true);
    expect(await screen.findByText(/was not rolled back/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same request again' })).not.toBeInTheDocument();
    expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).toContain(record.idempotencyKey);
  });

  it('does not describe a confirmed allocation as not sent when cleanup fails', async () => {
    signIn();
    const original = Storage.prototype.removeItem;
    const remove = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, key: string) {
      if (key === cookieRecoveryKeys.CreateAllocation) {
        throw new Error('locked');
      }

      return original.call(this, key);
    });
    server.use(http.post('/api/allocations', () => HttpResponse.json(successBody(false), { status: 201 })));
    renderAllocation();
    fireEvent.change(screen.getByLabelText('Invoice'), { target: { value: invoiceA } });
    fireEvent.change(screen.getByLabelText('Bank transaction'), { target: { value: bankA } });
    fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '10.50' } });
    fireEvent.click(screen.getByRole('button', { name: 'Allocate an amount' }));
    expect(await screen.findByText(/could not remove the local recovery record/i)).toBeVisible();
    expect(screen.queryByText(/not sent/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/rolled back/i)).toBeInTheDocument();
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain(invoiceA);
    remove.mockRestore();
  });
});

function renderDialog(item: { invoiceId: string; bankTransactionId: string; amount: DecimalString; currency: 'PLN'; normalizedReference: string; reason: string }) {
  return render(
    <QueryClientProvider client={queryClient}>
      <CookieConfirmationDialog item={item} onClose={() => undefined} />
    </QueryClientProvider>,
  );
}

function renderAllocation() {
  return render(<QueryClientProvider client={queryClient}><CookieAllocationForm /></QueryClientProvider>);
}

function renderReversal(allocationId: string) {
  return render(
    <QueryClientProvider client={queryClient}>
      <CookieReversalControl allocationId={allocationId} invoiceId={invoiceA} bankTransactionId={bankA} amount={readMoneyAmount('10.5')} currency="PLN" />
    </QueryClientProvider>,
  );
}

function renderRecovery() {
  return render(<QueryClientProvider client={queryClient}><CookieReversalRecovery /></QueryClientProvider>);
}

function pair(invoiceId: string, bankTransactionId: string) {
  return {
    invoiceId,
    bankTransactionId,
    amount: readMoneyAmount('10.50'),
    currency: 'PLN' as const,
    normalizedReference: 'COOKIE',
    reason: 'ExactReferenceCurrencyAndOpenAmount',
  };
}

function successBody(replayed: boolean) {
  return {
    allocationId: createdAllocation,
    invoiceId: invoiceA,
    bankTransactionId: bankA,
    amount: { amount: 10.5, currency: 'PLN' },
    replayed,
  };
}

function cookieSession() {
  return {
    authenticated: true,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada',
    expiresAt: null,
    organizations: [{ organizationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', displayName: 'North' }],
    activeOrganization: { organizationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', displayName: 'North' },
    authorization: 'verified' as const,
    capabilities: ['invoices.read', 'reconciliation.confirm', 'allocations.create', 'allocations.reverse', 'allocations.read'],
  };
}

function signIn(): void {
  sessionStore.replaceCookieSession(cookieSession(), true);
}
