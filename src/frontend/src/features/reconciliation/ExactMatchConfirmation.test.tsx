import { QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';

import { StaleSessionError } from '../../shared/api/errors';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { ReconciliationQueuePage } from './ReconciliationQueuePage';
import { createConfirmationAttempt, postExactConfirmation } from './confirmation-api';

const invoiceId = '11111111-1111-1111-1111-111111111111';
const otherInvoiceId = '22222222-2222-2222-2222-222222222222';
const bankTransactionId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const otherBankTransactionId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const allocationId = 'cccccccc-cccc-cccc-cccc-cccccccccccc';

describe('exact match confirmation', () => {
  it('posts only the selected pair and a new idempotency key', async () => {
    const user = userEvent.setup();
    const requests: Array<{ method: string; body: string; key: string; authorization: string; contentType: string }> = [];
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', async ({ request }) => {
        requests.push({
          method: request.method,
          body: await request.text(),
          key: request.headers.get('Idempotency-Key') ?? '',
          authorization: request.headers.get('Authorization') ?? '',
          contentType: request.headers.get('Content-Type') ?? '',
        });
        return HttpResponse.json(confirmationBody(), { status: 201 });
      }),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);

    expect(screen.getByText('This match is not reserved')).toBeVisible();
    expect(screen.getByText(/can be out of date/)).toBeVisible();
    expect(screen.getByText(/full current open amount/)).toBeVisible();
    expect(screen.getByText(/does not check whether the pair is still the only exact match in the whole book/)).toBeVisible();
    expect(screen.getByText(/Amount in this read 10\.50 PLN/)).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));

    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(screen.getByText(/Amount recorded 10\.25 PLN/)).toBeVisible();
    expect(screen.getByText(`Allocation ${allocationId}`)).toBeVisible();
    expect(requests).toHaveLength(1);
    expect(requests[0]?.method).toBe('POST');
    expect(requests[0]?.authorization).toBe('Bearer memory-token');
    expect(requests[0]?.contentType).toContain('application/json');
    expect(requests[0]?.key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
    const payload = JSON.parse(requests[0]?.body ?? '{}') as Record<string, unknown>;
    expect(Object.keys(payload)).toEqual(['invoiceId', 'bankTransactionId']);
    expect(payload).toEqual({ invoiceId, bankTransactionId });
    expect(requests[0]?.body).not.toContain('organizationId');
    expect(requests[0]?.body).not.toContain('amount');
  });

  it('shows a replay as the same recorded operation', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', () => HttpResponse.json(confirmationBody({ replayed: true }), { status: 200 })),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));

    expect(await screen.findByText('This confirmation was already recorded.')).toBeVisible();
    expect(screen.getByText(/Amount recorded 10\.25 PLN/)).toBeVisible();
    expect(screen.getByText(`Allocation ${allocationId}`)).toBeVisible();
    expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument();
  });

  it('keeps one submission when confirm is clicked twice', async () => {
    const user = userEvent.setup();
    let calls = 0;
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', async () => {
        calls += 1;
        await gate;
        return HttpResponse.json(confirmationBody(), { status: 201 });
      }),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    const confirm = screen.getByRole('button', { name: 'Confirm allocation' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    await waitFor(() => expect(calls).toBe(1));
    expect(screen.getByText('Confirmation in progress')).toBeVisible();
    release?.();
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(calls).toBe(1);
  });

  it('retries an unknown outcome with the same key and body', async () => {
    const user = userEvent.setup();
    const keys: string[] = [];
    const bodies: string[] = [];
    server.use(
      http.get('/api/matches', () => json(book())),
      http.get('/api/matches/smart', () => json({
        algorithmVersion: 'smart-1',
        advisory: true,
        truncated: false,
        coverageLimited: false,
        suggestions: [],
      })),
      http.post('/api/matches/confirm', async ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '');
        bodies.push(await request.text());
        if (keys.length === 1) {
          return HttpResponse.error();
        }

        return HttpResponse.json(confirmationBody({ replayed: true }), { status: 200 });
      }),
    );
    renderQueue();
    const epoch = sessionStore.getSnapshot().epoch;
    queryClient.setQueryData(['invoices', epoch, 1, 50], { items: [] });
    queryClient.setQueryData(['matches', epoch, 'smart'], {
      algorithmVersion: 'smart-1',
      advisory: true,
      truncated: false,
      coverageLimited: false,
      suggestions: [],
    });
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));

    expect(await screen.findByText(/We don’t know whether this confirmation was saved/)).toBeVisible();
    expect(screen.getByText(/Reloading the browser discards this attempt/)).toBeVisible();
    expect(screen.getByText(/Leaving the Reconciliation page discards it too/)).toBeVisible();
    expect(screen.getByText(/new operation with a new idempotency key/)).toBeVisible();
    expect(screen.getByText(/cannot tell you whether this one was saved/)).toBeVisible();
    expect(screen.getByText(/does not prove whether this confirmation was saved/)).toBeVisible();
    expect(queryClient.getQueryState(['invoices', epoch, 1, 50])?.isInvalidated).toBe(false);
    expect(queryClient.getQueryState(['matches', epoch, 'smart'])?.isInvalidated).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Close' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(screen.getByRole('button', { name: 'Review the confirmation attempt' })).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Smart' }));
    expect(screen.getByText(/Reloading the browser discards this attempt/)).toBeVisible();
    expect(screen.queryByRole('button', { name: /Confirm exact match/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Exact' }));
    expect(screen.getByRole('button', { name: confirmName(otherInvoiceId, otherBankTransactionId) })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Review the confirmation attempt' }));
    await user.click(screen.getByRole('button', { name: 'Send the same confirmation again' }));

    expect(await screen.findByText('This confirmation was already recorded.')).toBeVisible();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
    expect(bodies[0]).toBe(bodies[1]);
  });

  it('treats an unreadable success as uncertain and preserves the key', async () => {
    const user = userEvent.setup();
    const keys: string[] = [];
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '');
        if (keys.length === 1) {
          return HttpResponse.json(confirmationBody({ replayed: true }), { status: 201 });
        }

        return HttpResponse.json(confirmationBody(), { status: 201 });
      }),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));

    expect(await screen.findByText(/We don’t know whether this confirmation was saved/)).toBeVisible();
    expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Send the same confirmation again' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(keys[0]).toBe(keys[1]);
  });

  it('accepts a positive four-decimal amount and matches identifiers without case', async () => {
    const user = userEvent.setup();
    const body = `{"allocationId":"${allocationId.toUpperCase()}","invoiceId":"${invoiceId.toUpperCase()}","bankTransactionId":"${bankTransactionId.toUpperCase()}","amount":{"amount":10.2501,"currency":"PLN"},"replayed":false}`;
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', () => new HttpResponse(body, {
        status: 201,
        headers: { 'Content-Type': 'application/json' },
      })),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));

    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(screen.getByText('Amount recorded 10.2501 PLN')).toBeVisible();
    expect(screen.getByText(`Allocation ${allocationId.toUpperCase()}`)).toBeVisible();
  });

  it('keeps the original key when a success response is not a positive allocation', async () => {
    const user = userEvent.setup();
    const emptyGuid = '00000000-0000-0000-0000-000000000000';
    const invalidResponses = [
      confirmationBody({ amount: { amount: -1.25, currency: 'PLN' } }),
      confirmationBody({ amount: { amount: 0, currency: 'PLN' } }),
      confirmationBody({ amount: { amount: -0.0001, currency: 'PLN' } }),
      rawConfirmation({ amount: '0.0000' }),
      confirmationBody({ allocationId: emptyGuid }),
      confirmationBody({ invoiceId: emptyGuid }),
      confirmationBody({ bankTransactionId: emptyGuid }),
    ];

    for (const invalid of invalidResponses) {
      const keys: string[] = [];
      server.use(
        http.get('/api/matches', () => json(book())),
        http.post('/api/matches/confirm', ({ request }) => {
          keys.push(request.headers.get('Idempotency-Key') ?? '');
          if (keys.length === 1) {
            return typeof invalid === 'string'
              ? new HttpResponse(invalid, { status: 201, headers: { 'Content-Type': 'application/json' } })
              : HttpResponse.json(invalid, { status: 201 });
          }

          return HttpResponse.json(confirmationBody(), { status: 201 });
        }),
      );
      const view = renderQueue();
      const epoch = sessionStore.getSnapshot().epoch;
      queryClient.setQueryData(['invoices', epoch, 1, 50], { items: [] });
      queryClient.setQueryData(['matches', epoch, 'smart'], {
        algorithmVersion: 'smart-1',
        advisory: true,
        truncated: false,
        coverageLimited: false,
        suggestions: [],
      });
      await openExact(user, invoiceId, bankTransactionId);
      await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));

      expect(await screen.findByText(/We don’t know whether this confirmation was saved/)).toBeVisible();
      expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument();
      expect(queryClient.getQueryState(['invoices', epoch, 1, 50])?.isInvalidated).toBe(false);
      expect(queryClient.getQueryState(['matches', epoch, 'smart'])?.isInvalidated).toBe(false);

      await user.click(screen.getByRole('button', { name: 'Send the same confirmation again' }));
      expect(await screen.findByText('Allocation recorded.')).toBeVisible();
      expect(keys).toHaveLength(2);
      expect(keys[0]).toBe(keys[1]);
      expect(keys[0]).not.toBe(emptyGuid);
      view.unmount();
    }
  });

  it('rejects a mismatched id, an invalid allocation id, a zero amount, and a status that disagrees with replayed', async () => {
    const user = userEvent.setup();
    const responses = [
      confirmationBody({ invoiceId: otherInvoiceId }),
      confirmationBody({ allocationId: 'not-a-guid' }),
      confirmationBody({ amount: { amount: 0, currency: 'PLN' } }),
      confirmationBody({ replayed: true }),
    ];
    let index = 0;
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', () => HttpResponse.json(responses[index++] ?? confirmationBody(), { status: 201 })),
    );

    for (let step = 0; step < responses.length; step += 1) {
      const view = renderQueue();
      await openExact(user, invoiceId, bankTransactionId);
      await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));
      expect(await screen.findByText(/We don’t know whether this confirmation was saved/)).toBeVisible();
      expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument();
      view.unmount();
    }
  });

  it('does not create a new key after an idempotency payload conflict', async () => {
    const user = userEvent.setup();
    const keys: string[] = [];
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '');
        return problem(409, 'reconciliation.confirmation.idempotency_payload_conflict', 'Idempotency key was already used.', 'This idempotency key was already used for a different allocation request.');
      }),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));

    expect(await screen.findByText('A new idempotency key was not created.')).toBeVisible();
    expect(screen.getByText('reconciliation.confirmation.idempotency_payload_conflict')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Send the same confirmation again' }));
    await waitFor(() => expect(keys).toHaveLength(2));
    expect(keys[0]).toBe(keys[1]);
  });

  it('shows definitive rejections and keeps the original key for a manual retry', async () => {
    const user = userEvent.setup();
    const cases = [
      { status: 400, code: 'reconciliation.confirmation.invalid_request', title: 'Confirmation request is invalid.', detail: 'The confirmation body must contain only non-empty invoice and bank transaction ids.' },
      { status: 404, code: 'reconciliation.invoice.not_found', title: 'Invoice was not found.', detail: 'Invoice was not found.' },
      { status: 409, code: 'reconciliation.match.reference_mismatch', title: 'Payment references do not match.', detail: 'The invoice number and bank transaction reference are not an exact match.' },
      { status: 409, code: 'reconciliation.match.currency_mismatch', title: 'Currencies do not match.', detail: 'The invoice and bank transaction currencies do not match.' },
      { status: 409, code: 'reconciliation.match.stale', title: 'The match is stale.', detail: 'The invoice and bank transaction are no longer an exact open-amount match.' },
      { status: 409, code: 'reconciliation.concurrency_conflict', title: 'The financial records were modified.', detail: 'The invoice or bank transaction was modified by another operation.' },
    ];

    for (const item of cases) {
      const keys: string[] = [];
      server.use(
        http.get('/api/matches', () => json(book())),
        http.post('/api/matches/confirm', ({ request }) => {
          keys.push(request.headers.get('Idempotency-Key') ?? '');
          return problem(item.status, item.code, item.title, item.detail);
        }),
      );
      const view = renderQueue();
      await openExact(user, invoiceId, bankTransactionId);
      await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));
      expect(await screen.findByText(item.code)).toBeVisible();
      expect(screen.getAllByText(item.detail).length).toBeGreaterThan(0);
      expect(screen.getByText(/rejected before it was saved/)).toBeVisible();
      await user.click(screen.getByRole('button', { name: 'Send the same confirmation again' }));
      await waitFor(() => expect(keys).toHaveLength(2));
      expect(keys[0]).toBe(keys[1]);
      view.unmount();
    }
  });

  it('offers no confirmation action outside exact matches', async () => {
    const user = userEvent.setup();
    const calls: string[] = [];
    server.use(
      http.get('/api/matches', () => {
        calls.push('exact');
        return json(book());
      }),
      http.get('/api/matches/smart', () => {
        calls.push('smart');
        return json({
          algorithmVersion: 'smart-1',
          advisory: true,
          truncated: false,
          coverageLimited: false,
          suggestions: [{
            rank: 1,
            invoiceId,
            bankTransactionId,
            score: 95,
            scoreScale: 'points',
            contested: false,
            exactMatchAmbiguous: false,
            invoiceOutstanding: { amount: 25, currency: 'PLN' },
            bankTransactionUnapplied: { amount: 25, currency: 'PLN' },
            suggestedAmount: { amount: 25, currency: 'PLN' },
            signals: [
              { code: 'ReferenceExact', points: 70 },
              { code: 'AmountExact', points: 25 },
            ],
          }],
        });
      }),
      http.post('/api/matches/confirm', () => {
        calls.push('post');
        return HttpResponse.json(confirmationBody(), { status: 201 });
      }),
    );
    renderQueue();

    expect(await screen.findByRole('button', { name: confirmName(invoiceId, bankTransactionId) })).toBeEnabled();
    expect(within(screen.getByRole('region', { name: 'Exact matches' })).getAllByRole('button', { name: /Confirm exact match/ })).toHaveLength(2);
    expect(within(screen.getByRole('region', { name: 'Ambiguous groups' })).queryByRole('button', { name: /Confirm exact match/ })).not.toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Unmatched' })).queryByRole('button', { name: /Confirm exact match/ })).not.toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Ineligible' })).queryByRole('button', { name: /Confirm exact match/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Smart' }));
    expect(await screen.findByRole('cell', { name: '95 points' })).toBeVisible();
    expect(screen.queryByRole('button', { name: /Confirm exact match/ })).not.toBeInTheDocument();
    expect(calls).not.toContain('post');
  });

  it('invalidates invoice and match caches after a verified 201 and a verified 200', async () => {
    const user = userEvent.setup();
    let replay = false;
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', () => HttpResponse.json(
        confirmationBody({ replayed: replay }),
        { status: replay ? 200 : 201 },
      )),
    );
    const created = renderQueue();
    const createdEpoch = sessionStore.getSnapshot().epoch;
    queryClient.setQueryData(['invoices', createdEpoch, 1, 50], { items: [] });
    queryClient.setQueryData(['bank-transactions', createdEpoch, 1, 50], { items: [] });
    queryClient.setQueryData(['matches', createdEpoch, 'smart'], { algorithmVersion: 'smart-1' });
    queryClient.setQueryData(['allocations', 'history', createdEpoch, 1, 50, invoiceId, ''], { items: [] });
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    await waitFor(() => {
      expect(queryClient.getQueryState(['invoices', createdEpoch, 1, 50])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['bank-transactions', createdEpoch, 1, 50])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['matches', createdEpoch, 'smart'])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['allocations', 'history', createdEpoch, 1, 50, invoiceId, ''])?.isInvalidated).toBe(true);
    });
    created.unmount();

    replay = true;
    const replayed = renderQueue();
    const replayEpoch = sessionStore.getSnapshot().epoch;
    queryClient.setQueryData(['invoices', replayEpoch, 1, 50], { items: [] });
    queryClient.setQueryData(['bank-transactions', replayEpoch, 1, 50], { items: [] });
    queryClient.setQueryData(['matches', replayEpoch, 'smart'], { algorithmVersion: 'smart-1' });
    queryClient.setQueryData(['allocations', 'history', replayEpoch, 2, 25, '', bankTransactionId], { items: [] });
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    expect(await screen.findByText('This confirmation was already recorded.')).toBeVisible();
    await waitFor(() => {
      expect(queryClient.getQueryState(['invoices', replayEpoch, 1, 50])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['bank-transactions', replayEpoch, 1, 50])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['matches', replayEpoch, 'smart'])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['allocations', 'history', replayEpoch, 2, 25, '', bankTransactionId])?.isInvalidated).toBe(true);
    });
    replayed.unmount();
  });

  it('stops an in-flight confirmation when the session changes and does not replay it', async () => {
    const user = userEvent.setup();
    let calls = 0;
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', async () => {
        calls += 1;
        await gate;
        return HttpResponse.json(confirmationBody(), { status: 201 });
      }),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    expect(await screen.findByText('Confirmation in progress')).toBeVisible();
    expect(calls).toBe(1);

    sessionStore.rotate('replacement-token', Math.floor(Date.now() / 1000) + 3600, null);
    const nextEpoch = sessionStore.getSnapshot().epoch;
    queryClient.setQueryData(['invoices', nextEpoch, 1, 50], { kept: true });
    queryClient.setQueryData(['matches', nextEpoch, 'smart'], { algorithmVersion: 'smart-1' });
    release?.();

    expect(await screen.findByText(/was not sent again under the new sign-in/)).toBeVisible();
    expect(screen.getByText(/does not show that the server rolled the attempt back/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Send the same confirmation again' })).not.toBeInTheDocument();
    expect(queryClient.getQueryState(['invoices', nextEpoch, 1, 50])?.isInvalidated).toBe(false);
    expect(queryClient.getQueryState(['matches', nextEpoch, 'smart'])?.isInvalidated).toBe(false);
    expect(calls).toBe(1);
  });

  it('discards an unsubmitted draft when the session changes', async () => {
    const user = userEvent.setup();
    const tenantBInvoiceId = '33333333-3333-3333-3333-333333333333';
    const tenantBBankTransactionId = 'dddddddd-dddd-dddd-dddd-dddddddddddd';
    const requests: Array<{ authorization: string; body: string }> = [];
    server.use(
      http.get('/api/matches', ({ request }) => {
        if (request.headers.get('Authorization') === 'Bearer replacement-token') {
          return json(exactOnlyBook('REF-B', tenantBInvoiceId, tenantBBankTransactionId, 4.5));
        }

        return json(book());
      }),
      http.post('/api/matches/confirm', async ({ request }) => {
        const body = await request.text();
        requests.push({
          authorization: request.headers.get('Authorization') ?? '',
          body,
        });
        const payload = JSON.parse(body) as { invoiceId: string; bankTransactionId: string };
        return HttpResponse.json(confirmationBody({
          allocationId: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
          invoiceId: payload.invoiceId,
          bankTransactionId: payload.bankTransactionId,
          amount: { amount: 4.5, currency: 'PLN' },
        }), { status: 201 });
      }),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    expect(screen.getByRole('dialog', { name: 'Confirm exact match' })).toBeVisible();
    expect(screen.getByText(`Invoice ${invoiceId}`)).toBeVisible();
    expect(screen.getByText(`Bank transaction ${bankTransactionId}`)).toBeVisible();
    expect(screen.getByText(/Amount in this read 10\.50 PLN/)).toBeVisible();

    sessionStore.rotate('replacement-token', Math.floor(Date.now() / 1000) + 3600, null);

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Confirm exact match' })).not.toBeInTheDocument();
    });
    expect(screen.queryByText(`Invoice ${invoiceId}`)).not.toBeInTheDocument();
    expect(screen.queryByText(`Bank transaction ${bankTransactionId}`)).not.toBeInTheDocument();
    expect(screen.queryByText(/Amount in this read/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Confirm allocation' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: confirmName(invoiceId, bankTransactionId) })).not.toBeInTheDocument();
    expect(requests).toHaveLength(0);

    expect(await screen.findByRole('button', { name: confirmName(tenantBInvoiceId, tenantBBankTransactionId) })).toBeVisible();
    await openExact(user, tenantBInvoiceId, tenantBBankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));

    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(requests).toHaveLength(1);
    expect(requests[0]?.authorization).toBe('Bearer replacement-token');
    expect(JSON.parse(requests[0]?.body ?? '{}')).toEqual({
      invoiceId: tenantBInvoiceId,
      bankTransactionId: tenantBBankTransactionId,
    });
    expect(requests[0]?.body).not.toContain(invoiceId);
    expect(requests[0]?.body).not.toContain(bankTransactionId);
  });

  it('does not send a confirmation captured for a session that has rotated', async () => {
    let calls = 0;
    server.use(http.post('/api/matches/confirm', () => {
      calls += 1;
      return HttpResponse.json(confirmationBody(), { status: 201 });
    }));
    sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
    const attempt = createConfirmationAttempt(invoiceId, bankTransactionId, sessionStore.getSnapshot().epoch);
    sessionStore.rotate('replacement-token', Math.floor(Date.now() / 1000) + 3600, null);

    await expect(postExactConfirmation(attempt, new AbortController().signal)).rejects.toBeInstanceOf(StaleSessionError);
    expect(calls).toBe(0);
  });

  it('shows unauthorized and forbidden confirmations without another key', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', () => problem(401, null, 'Unauthorized', '')),
    );
    const unauthorized = renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    expect(await screen.findByText(/Your access has expired or is no longer valid/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Send the same confirmation again' })).not.toBeInTheDocument();
    unauthorized.unmount();

    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', () => problem(403, 'reconciliation.actor.required', 'Actor is required.', 'A valid authenticated actor is required.')),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    expect(await screen.findByText(/You don’t have access to this workspace/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Send the same confirmation again' })).not.toBeInTheDocument();
  });

  it('keeps an unexpected server error uncertain', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/matches', () => json(book())),
      http.post('/api/matches/confirm', () => problem(500, null, 'Internal processing failure', 'Diagnostic-only text.')),
    );
    renderQueue();
    await openExact(user, invoiceId, bankTransactionId);
    await user.click(screen.getByRole('button', { name: 'Confirm allocation' }));

    expect(await screen.findByText(/does not show whether the allocation was saved/)).toBeVisible();
    expect(screen.getByText(/Internal processing failure/)).toBeVisible();
    expect(screen.getByText(/Diagnostic-only text/)).toBeVisible();
    expect(screen.queryByText(/rejected before it was saved/)).not.toBeInTheDocument();
  });
});

function renderQueue() {
  sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ReconciliationQueuePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function openExact(user: ReturnType<typeof userEvent.setup>, invoice: string, bankTransaction: string) {
  await user.click(await screen.findByRole('button', { name: confirmName(invoice, bankTransaction) }));
}

function confirmName(invoice: string, bankTransaction: string): string {
  return `Confirm exact match for invoice ${invoice} and bank transaction ${bankTransaction}`;
}

function json(body: object) {
  return HttpResponse.json(body);
}

function problem(status: number, code: string | null, title: string, detail: string) {
  return HttpResponse.json({ title, detail, code }, { status });
}

function confirmationBody(overrides: Record<string, unknown> = {}) {
  return {
    allocationId,
    invoiceId,
    bankTransactionId,
    amount: { amount: 10.25, currency: 'PLN' },
    replayed: false,
    ...overrides,
  };
}

function rawConfirmation(overrides: { amount: string }): string {
  return `{"allocationId":"${allocationId}","invoiceId":"${invoiceId}","bankTransactionId":"${bankTransactionId}","amount":{"amount":${overrides.amount},"currency":"PLN"},"replayed":false}`;
}

function exactOnlyBook(reference: string, invoice: string, bankTransaction: string, amount: number) {
  return {
    algorithmVersion: 'exact-1',
    exactMatches: [exactMatch(reference, invoice, bankTransaction, amount)],
    ambiguousGroups: [],
    unmatched: [],
    ineligible: [],
  };
}

function book() {
  return {
    algorithmVersion: 'exact-1',
    exactMatches: [
      exactMatch('REF-Z', invoiceId, bankTransactionId, 10.5),
      exactMatch('REF-Y', otherInvoiceId, otherBankTransactionId, 8),
    ],
    ambiguousGroups: [{
      normalizedReference: 'REF-A',
      openAmount: { amount: 20, currency: 'PLN' },
      reason: 'CompetingInvoices',
      invoiceIds: [invoiceId, otherInvoiceId],
      bankTransactionIds: [otherBankTransactionId],
    }],
    unmatched: [{
      classification: 'NoMatch',
      reason: 'NoCounterpart',
      invoiceId,
      bankTransactionId: null,
      normalizedReference: 'REF-U',
      openAmount: { amount: 5, currency: 'PLN' },
    }],
    ineligible: [{
      classification: 'Ineligible',
      reason: 'MissingReference',
      invoiceId: null,
      bankTransactionId,
      normalizedReference: null,
      openAmount: { amount: 0, currency: 'PLN' },
    }],
  };
}

function exactMatch(reference: string, invoice: string, bankTransaction: string, amount: number) {
  return {
    invoiceId: invoice,
    bankTransactionId: bankTransaction,
    amount: { amount, currency: 'PLN' },
    normalizedReference: reference,
    reason: 'ExactReferenceCurrencyAndOpenAmount',
  };
}
