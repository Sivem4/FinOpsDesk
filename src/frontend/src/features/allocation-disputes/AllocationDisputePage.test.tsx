import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, MemoryRouter, RouterProvider } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AppRouter } from '../../app/router';
import { CookieFinancialRouter } from '../../app/CookieFinancialRouter';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore, type CookieSessionFields } from '../../shared/api/session-store';
import { cookieNavLinks } from '../../shared/auth/cookie-navigation';
import { AppShell } from '../../shared/ui/AppShell';
import { fakeJwt } from '../../test/jwt';
import { server } from '../../test/server';
import { AllocationDisputePage } from './AllocationDisputePage';

const allocationA = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const allocationB = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const disputeA = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const disputeB = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const disputeC = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';

afterEach(() => {
  vi.unstubAllEnvs();
});

describe('allocation dispute screen', () => {
  it('shows the development navigation link and the route', async () => {
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    server.use(http.get('/api/allocation-disputes', () => text(listDocument([registration()]))));
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/allocation-disputes']}>
          <AppShell onLogout={() => undefined}>
            <AppRouter />
          </AppShell>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(screen.getByRole('link', { name: 'Allocation disputes' })).toHaveAttribute('href', '/allocation-disputes');
    expect(await screen.findByRole('heading', { name: 'Allocation disputes' })).toBeInTheDocument();
    expect(await screen.findByText('counterparty challenge')).toBeInTheDocument();
  });

  it('sends the allocation filter only after submit and never sends the selection', async () => {
    const user = userEvent.setup();
    const listUrls: string[] = [];
    server.use(
      http.get('/api/allocation-disputes', ({ request }) => {
        listUrls.push(request.url);
        const allocationId = new URL(request.url).searchParams.get('allocationId');
        const statement = allocationId === null ? 'before submit' : 'after submit';
        return text(listDocument([registration({ statement })], allocationId === null ? '1' : '51'));
      }),
      http.get('/api/allocation-disputes/:disputeId', () => text(disputeDocument())),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/allocation-disputes?selected=${disputeA}`);

    expect(await screen.findByText('before submit')).toBeInTheDocument();
    expect(listUrls.every((url) => !new URL(url).searchParams.has('allocationId'))).toBe(true);
    expect(listUrls.every((url) => !new URL(url).searchParams.has('selected'))).toBe(true);
    await user.type(screen.getByLabelText('Allocation id'), allocationA);
    expect(listUrls.every((url) => !new URL(url).searchParams.has('allocationId'))).toBe(true);
    await user.click(screen.getByRole('button', { name: 'Apply filter' }));
    expect(await screen.findByText('after submit')).toBeInTheDocument();
    const submitted = new URL(listUrls.at(-1) ?? '');
    expect(submitted.searchParams.get('allocationId')).toBe(allocationA);
    expect(submitted.searchParams.get('page')).toBe('1');
    expect(submitted.searchParams.has('selected')).toBe(false);
    expect(await screen.findByRole('region', { name: 'Allocation dispute registration' })).toBeInTheDocument();
  });

  it('reads a dispute that is not on the current page', async () => {
    const detailUrls: string[] = [];
    server.use(
      http.get('/api/allocation-disputes', () => text(listDocument([
        registration({ statement: 'on-page registration' }),
      ]))),
      http.get('/api/allocation-disputes/:disputeId', ({ request }) => {
        detailUrls.push(request.url);
        return text(disputeDocument({
          disputeId: disputeC,
          statement: 'off-page registration',
        }));
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/allocation-disputes?selected=${disputeC}`);

    expect(await screen.findByText('on-page registration')).toBeInTheDocument();
    const detail = await screen.findByRole('region', { name: 'Allocation dispute registration' });
    expect(within(detail).getByText('off-page registration')).toBeInTheDocument();
    expect(detailUrls.some((url) => url.includes(disputeC))).toBe(true);
  });

  it('keeps the filter and selection across page buttons and browser history', async () => {
    const user = userEvent.setup();
    const listUrls: string[] = [];
    server.use(
      http.get('/api/allocation-disputes', ({ request }) => {
        listUrls.push(request.url);
        const url = new URL(request.url);
        const allocationId = url.searchParams.get('allocationId');
        const page = Number(url.searchParams.get('page') ?? '1');
        if (allocationId === allocationB) {
          return text(listDocument([
            registration({
              disputeId: disputeB,
              allocationId: allocationB,
              statement: 'statement-b',
            }),
          ], '51', page));
        }

        if (allocationId === allocationA) {
          return text(listDocument([
            registration({
              statement: page === 2 ? 'statement-a-page-2' : 'statement-a',
            }),
          ], '51', page));
        }

        return text(listDocument([registration({ statement: 'statement-a' })]));
      }),
      http.get('/api/allocation-disputes/:disputeId', ({ params }) => text(disputeDocument({
        disputeId: String(params.disputeId),
        allocationId: params.disputeId === disputeB ? allocationB : allocationA,
        statement: params.disputeId === disputeB ? 'detail-b' : 'detail-a',
      }))),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const router = renderRouter();
    expect(await screen.findByText('statement-a')).toBeInTheDocument();

    await user.type(screen.getByLabelText('Allocation id'), allocationA);
    await user.click(screen.getByRole('button', { name: 'Apply filter' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Next page' })).toBeEnabled());
    await user.click(screen.getByRole('button', { name: `Show allocation dispute ${disputeA}` }));
    expect(await screen.findByText('detail-a')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Next page' }));
    await waitFor(() => expect(router.state.location.search).toContain('page=2'));
    expect(router.state.location.search).toContain(`allocationId=${allocationA}`);
    expect(router.state.location.search).toContain(`selected=${disputeA}`);
    expect(await screen.findByText('statement-a-page-2')).toBeInTheDocument();

    await user.clear(screen.getByLabelText('Allocation id'));
    await user.type(screen.getByLabelText('Allocation id'), allocationB);
    await user.click(screen.getByRole('button', { name: 'Apply filter' }));
    expect(await screen.findByText('statement-b')).toBeInTheDocument();
    expect(router.state.location.search).toContain('page=1');
    expect(router.state.location.search).toContain(`allocationId=${allocationB}`);
    expect(router.state.location.search).toContain(`selected=${disputeA}`);
    await user.click(screen.getByRole('button', { name: `Show allocation dispute ${disputeB}` }));
    expect(await screen.findByText('detail-b')).toBeInTheDocument();

    await router.navigate(-1);
    await waitFor(() => expect(router.state.location.search).toContain(`selected=${disputeA}`));
    expect(router.state.location.search).toContain(`allocationId=${allocationB}`);
    expect(screen.getByLabelText('Allocation id')).toHaveValue(allocationB);
    await user.clear(screen.getByLabelText('Allocation id'));
    await user.type(screen.getByLabelText('Allocation id'), 'draft-value');
    await user.click(screen.getByRole('button', { name: `Show allocation dispute ${disputeB}` }));
    expect(screen.getByLabelText('Allocation id')).toHaveValue('draft-value');

    await router.navigate(-1);
    await waitFor(() => expect(router.state.location.search).toContain(`selected=${disputeA}`));
    expect(screen.getByLabelText('Allocation id')).toHaveValue('draft-value');
    await router.navigate(-1);
    await waitFor(() => expect(router.state.location.search).toContain(`allocationId=${allocationA}`));
    await waitFor(() => expect(screen.getByLabelText('Allocation id')).toHaveValue(allocationA));
    expect(router.state.location.search).toContain('page=2');
    expect(router.state.location.search).toContain(`selected=${disputeA}`);
    expect(listUrls.every((url) => !new URL(url).searchParams.has('selected'))).toBe(true);
  });

  it('drops a late list from the previous session', async () => {
    const gate = deferred<void>();
    let calls = 0;
    server.use(http.get('/api/allocation-disputes', async () => {
      calls += 1;
      if (calls === 1) {
        await gate.promise;
        return text(listDocument([registration({ statement: 'stale registration' })]));
      }

      return text(listDocument([registration({ statement: 'current registration' })]));
    }));
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage('/allocation-disputes');
    await waitFor(() => expect(calls).toBe(1));
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    expect(await screen.findByText('current registration')).toBeInTheDocument();
    gate.resolve();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByText('stale registration')).not.toBeInTheDocument();
    expect(screen.getByText('current registration')).toBeInTheDocument();
  });

  it('distinguishes an empty organization, an empty allocation, and a page past the rows', async () => {
    server.use(http.get('/api/allocation-disputes', ({ request }) => {
      const allocationId = new URL(request.url).searchParams.get('allocationId');
      const page = new URL(request.url).searchParams.get('page');
      if (page === '2') {
        return text(listDocument([], '5', 2));
      }

      return text(listDocument([], allocationId === null ? '0' : '0'));
    }));
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const organization = renderPage('/allocation-disputes');
    expect(await screen.findByText('No allocation disputes are stored for this organization.')).toBeInTheDocument();
    organization.unmount();

    const allocation = renderPage(`/allocation-disputes?allocationId=${allocationA}`);
    expect(await screen.findByText('No allocation disputes are stored for this allocation.')).toBeInTheDocument();
    expect(screen.queryByText('No allocation disputes are stored for this organization.')).not.toBeInTheDocument();
    allocation.unmount();

    renderPage('/allocation-disputes?page=2');
    expect(await screen.findByText('This page has no stored allocation disputes. Dispute count 5.')).toBeInTheDocument();
    expect(screen.queryByText('No allocation disputes are stored for this organization.')).not.toBeInTheDocument();
  });

  it('offers update access on HTTP 401 and keeps the token on HTTP 403', async () => {
    const user = userEvent.setup();
    const token = fakeJwt();
    sessionStore.rotate(token, 4_000_000_000, null);
    server.use(http.get('/api/allocation-disputes', () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 })));
    const { unmount } = renderPage('/allocation-disputes');
    expect(await screen.findByText('You don’t have access to allocation disputes.')).toBeInTheDocument();
    expect(sessionStore.getSnapshot().token).toBe(token);
    expect(screen.getByRole('heading', { name: 'Allocation disputes' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Update access' })).not.toBeInTheDocument();
    expect(screen.queryByText('counterparty challenge')).not.toBeInTheDocument();
    unmount();

    server.use(http.get('/api/allocation-disputes', () => HttpResponse.json({ title: 'Unauthorized' }, { status: 401 })));
    renderPage('/allocation-disputes');
    await user.click(await screen.findByRole('button', { name: 'Update access' }));
    expect(sessionStore.getSnapshot().token).toBeNull();
    expect(await screen.findByText('Allocation disputes are not available for this session.')).toBeInTheDocument();
    expect(screen.queryByText('counterparty challenge')).not.toBeInTheDocument();
  });

  it.each([
    {
      label: 'HTTP 401',
      failure: () => HttpResponse.json({ title: 'Unauthorized' }, { status: 401 }),
      message: 'Your access has expired or is no longer valid. Update it to continue.',
      retry: false,
      updateAccess: true,
      keepsToken: false,
    },
    {
      label: 'HTTP 403',
      failure: () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 }),
      message: 'You don’t have access to allocation disputes.',
      retry: true,
      updateAccess: false,
      keepsToken: true,
    },
    {
      label: 'HTTP 404',
      failure: () => HttpResponse.json({ title: 'Not found' }, { status: 404 }),
      message: 'That allocation dispute was not found.',
      retry: false,
      updateAccess: false,
      keepsToken: false,
    },
    {
      label: 'HTTP 500',
      failure: () => HttpResponse.json({ title: 'Server error' }, { status: 500 }),
      message: 'Allocation disputes could not be loaded.',
      retry: true,
      updateAccess: false,
      keepsToken: false,
    },
    {
      label: 'a defective HTTP 200',
      failure: () => text('{"items":[]}'),
      message: 'We couldn’t read this allocation dispute response.',
      retry: true,
      updateAccess: false,
      keepsToken: false,
    },
  ])('hides the previous registration after the detail refetch fails with $label', async (variant) => {
    const user = userEvent.setup();
    const token = fakeJwt();
    let detailMode: 'success' | 'failure' = 'success';
    server.use(
      http.get('/api/allocation-disputes', () => text(listDocument([
        registration({ statement: 'listed registration' }),
      ]))),
      http.get('/api/allocation-disputes/:disputeId', () => (
        detailMode === 'success'
          ? text(disputeDocument({ statement: 'stored registration' }))
          : variant.failure()
      )),
    );
    sessionStore.rotate(token, 4_000_000_000, null);
    renderPage(`/allocation-disputes?selected=${disputeA}`);

    const detail = await screen.findByRole('region', { name: 'Allocation dispute registration' });
    expect(await within(detail).findByText('stored registration')).toBeInTheDocument();
    expect(within(detail).getByText('Actor source developmentJwt')).toBeInTheDocument();
    expect(within(detail).getByText('e2e-operator')).toBeInTheDocument();
    expect(screen.getByText('listed registration')).toBeInTheDocument();

    detailMode = 'failure';
    await queryClient.refetchQueries({
      queryKey: ['allocation-disputes', 'detail', sessionStore.getSnapshot().epoch, disputeA],
    });

    expect(await within(detail).findByText(variant.message)).toBeInTheDocument();
    expect(within(detail).queryByText('stored registration')).not.toBeInTheDocument();
    expect(within(detail).queryByText('Actor source developmentJwt')).not.toBeInTheDocument();
    expect(within(detail).queryByText('e2e-operator')).not.toBeInTheDocument();
    expect(screen.getByText('listed registration')).toBeInTheDocument();
    if (variant.keepsToken) {
      expect(sessionStore.getSnapshot().token).toBe(token);
    }

    if (variant.updateAccess) {
      expect(within(detail).getByRole('button', { name: 'Update access' })).toBeInTheDocument();
    }

    if (variant.retry) {
      detailMode = 'success';
      await user.click(within(detail).getByRole('button', { name: 'Try again' }));
      expect(await within(detail).findByText('stored registration')).toBeInTheDocument();
      expect(within(detail).getByText('e2e-operator')).toBeInTheDocument();
      expect(within(detail).queryByText(variant.message)).not.toBeInTheDocument();
    }
  });

  it('keeps the list when the selected dispute is missing', async () => {
    server.use(
      http.get('/api/allocation-disputes', () => text(listDocument([registration()]))),
      http.get('/api/allocation-disputes/:disputeId', () => HttpResponse.json({ title: 'Not found' }, { status: 404 })),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/allocation-disputes?selected=${disputeC}`);
    expect(await screen.findByText('counterparty challenge')).toBeInTheDocument();
    expect(await screen.findByText('That allocation dispute was not found.')).toBeInTheDocument();
  });

  it('retries a transport failure and rejects a body that is not the registration', async () => {
    const user = userEvent.setup();
    let calls = 0;
    server.use(http.get('/api/allocation-disputes', () => {
      calls += 1;
      if (calls === 1) {
        return HttpResponse.error();
      }

      return text(listDocument([registration()]));
    }));
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const { unmount } = renderPage('/allocation-disputes');
    await user.click(await screen.findByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('counterparty challenge')).toBeInTheDocument();
    unmount();

    server.use(http.get('/api/allocation-disputes', () => text('{"items":[]}')));
    renderPage('/allocation-disputes');
    expect(await screen.findByText('We couldn’t read this allocation dispute response.')).toBeInTheDocument();
    expect(screen.queryByText('No allocation disputes are stored for this organization.')).not.toBeInTheDocument();
  });

  it('hides a structurally valid response that names a different id', async () => {
    server.use(
      http.get('/api/allocation-disputes', ({ request }) => {
        const allocationId = new URL(request.url).searchParams.get('allocationId');
        if (allocationId !== null) {
          return text(listDocument([registration({
            allocationId: allocationB,
            statement: 'wrong allocation',
          })]));
        }

        return text(listDocument([registration()]));
      }),
      http.get('/api/allocation-disputes/:disputeId', () => text(disputeDocument({
        disputeId: disputeB,
        statement: 'foreign registration',
      }))),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const { unmount } = renderPage(`/allocation-disputes?allocationId=${allocationA}`);
    expect(await screen.findByText('We couldn’t read this allocation dispute response.')).toBeInTheDocument();
    expect(screen.queryByText('wrong allocation')).not.toBeInTheDocument();
    expect(screen.queryByText('No allocation disputes are stored for this allocation.')).not.toBeInTheDocument();
    unmount();

    renderPage(`/allocation-disputes?selected=${disputeA}`);
    expect(await screen.findByText('counterparty challenge')).toBeInTheDocument();
    expect(await screen.findByText('We couldn’t read this allocation dispute response.')).toBeInTheDocument();
    expect(screen.queryByText('foreign registration')).not.toBeInTheDocument();
  });

  it('accepts the same GUID in another case and blocks an invalid address', async () => {
    const listUrls: string[] = [];
    server.use(
      http.get('/api/allocation-disputes', ({ request }) => {
        listUrls.push(request.url);
        return text(listDocument([registration({ allocationId: allocationA })]));
      }),
      http.get('/api/allocation-disputes/:disputeId', () => text(disputeDocument({
        disputeId: disputeA.toUpperCase(),
      }))),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const matching = renderPage(`/allocation-disputes?allocationId=${allocationA.toUpperCase()}&selected=${disputeA}`);
    const detail = await screen.findByRole('region', { name: 'Allocation dispute registration' });
    expect(await within(detail).findByText('counterparty challenge')).toBeInTheDocument();
    expect(await within(detail).findByText(disputeA.toUpperCase())).toBeInTheDocument();
    matching.unmount();

    listUrls.length = 0;
    const invalidPage = renderPage('/allocation-disputes?page=42949674&sort=recordedAt');
    expect(await screen.findByText('This allocation dispute address can’t be opened.')).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(listUrls).toEqual([]);
    invalidPage.unmount();

    renderPage('/allocation-disputes?selected=not-a-guid&sort=recordedAt');
    expect(await screen.findByText('counterparty challenge')).toBeInTheDocument();
    expect(screen.getByText('The selected allocation dispute address is not valid. The list can still be read.')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Allocation dispute registration' })).not.toBeInTheDocument();
  });

  it('does not mount the screen or request disputes for a cookie session', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    const hits: string[] = [];
    const capabilities = [
      'invoices.read',
      'bank-transactions.read',
      'reconciliation.read',
      'allocations.read',
      'review-cases.read',
      'audit.read',
    ];
    sessionStore.replaceCookieSession(cookieSession(capabilities), true);
    server.use(
      http.get('/api/allocation-disputes', () => {
        hits.push('list');
        return text(listDocument([registration()]));
      }),
      http.get('/api/allocation-disputes/:disputeId', () => {
        hits.push('detail');
        return text(disputeDocument());
      }),
      http.get('/api/allocation-disputes/:disputeId/resolution', ({ params }) => {
        hits.push('resolution');
        return HttpResponse.json({ disputeId: params.disputeId, decision: null });
      }),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        hits.push('resolution-post');
        return new HttpResponse(null, { status: 500 });
      }),
    );
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/allocation-disputes']}>
          <AppShell onLogout={() => undefined} links={cookieNavLinks(capabilities)}>
            <CookieFinancialRouter />
          </AppShell>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('That page is not available.')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Allocation disputes' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Allocation disputes' })).not.toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(hits).toEqual([]);
  });
});

function renderPage(path: string) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <AllocationDisputePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function renderRouter() {
  const router = createMemoryRouter(
    [{ path: '/allocation-disputes', element: <AllocationDisputePage /> }],
    { initialEntries: ['/allocation-disputes'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

function text(body: string) {
  return HttpResponse.text(body, { headers: { 'Content-Type': 'application/json' } });
}

function listDocument(items: Record<string, unknown>[], totalCount = String(items.length), page = 1): string {
  return `{"items":${JSON.stringify(items)},"page":${page},"pageSize":50,"totalCount":${totalCount}}`;
}

function disputeDocument(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify(registration(overrides));
}

function registration(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    disputeId: disputeA,
    allocationId: allocationA,
    statement: 'counterparty challenge',
    actorSource: 'developmentJwt',
    actorId: 'e2e-operator',
    recordedAt: '2026-11-07T09:00:00Z',
    ...overrides,
  };
}

function cookieSession(capabilities: readonly string[]): CookieSessionFields {
  return {
    authenticated: true,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada Operator',
    expiresAt: null,
    organizations: [],
    activeOrganization: {
      organizationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      displayName: 'North Ledger',
    },
    authorization: 'verified',
    capabilities,
  };
}

function deferred<T>() {
  let resolve: (value: T) => void = () => {
    throw new Error('The gate was not started.');
  };
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}
