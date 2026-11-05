import { render, screen } from '@testing-library/react';
import { StrictMode } from 'react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  explicitAmountAttemptStorageKey,
  inspectAllocationAttempt,
  submitExplicitAmountAttempt,
} from '../../features/reconciliation/allocation-attempt';
import { queryClient } from '../api/query-client';
import { sessionStore } from '../api/session-store';
import { server } from '../../test/server';
import { AuthGate } from './AuthGate';
import { CookieAuthGate } from './CookieAuthGate';

afterEach(() => {
  vi.unstubAllEnvs();
});

describe('cookie session gate', () => {
  it('shows the development token screen by default', async () => {
    render(
      <AuthGate>
        <p>Invoice workspace</p>
      </AuthGate>,
    );

    expect(await screen.findByLabelText('Development access token', undefined, { timeout: 4_000 })).toBeInTheDocument();
  });

  it('uses the cookie session when development selects cookie mode', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    server.use(http.get('/api/session', () => HttpResponse.json(anonymousBody())));

    render(
      <AuthGate>
        <p>Invoice workspace</p>
      </AuthGate>,
    );

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByText('Invoice workspace')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Development access token')).not.toBeInTheDocument();
  });

  it('shows the sign-in form after the development session effect runs twice', async () => {
    server.use(http.get('/api/session', () => HttpResponse.json(anonymousBody())));
    render(
      <StrictMode>
        <CookieAuthGate />
      </StrictMode>,
    );

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByText('Checking the browser session')).not.toBeInTheDocument();
  });

  it('posts the anonymous CSRF token in a browser form', async () => {
    server.use(http.get('/api/session', () => HttpResponse.json(anonymousBody())));
    render(<CookieAuthGate />);

    const form = await screen.findByRole('form', { name: 'Sign in' });
    expect(form).toHaveAttribute('action', '/login');
    expect(form.getAttribute('method')?.toLowerCase()).toBe('post');
    expect(form.querySelector('input[name="__RequestVerificationToken"]')).toHaveValue('csrf-anonymous');
    expect(form.querySelector('input[name="returnUrl"]')).toHaveValue('/');
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('shows a signed-in operator without financial screens', async () => {
    server.use(http.get('/api/session', () => HttpResponse.json(restrictedBody())));
    render(<CookieAuthGate />);

    expect(await screen.findByRole('heading', { name: 'Signed in' })).toBeInTheDocument();
    expect(screen.getByText('Authentication succeeded. Financial access is not available for this organization.')).toBeInTheDocument();
    expect(screen.getByText('Signed in as Ada Operator')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Invoices' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Bank transactions' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Imports' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reconciliation' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Allocation history' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Review cases' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Review cases' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument();
  });

  it('keeps an expired cookie expiry on the restricted screen', async () => {
    server.use(
      http.get('/api/session', () =>
        HttpResponse.json({ ...restrictedBody(), expiresAt: '2020-01-01T00:00:00.000Z' }),
      ),
    );
    render(<CookieAuthGate />);

    expect(await screen.findByRole('heading', { name: 'Signed in' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument();
  });

  it('shows a session error instead of signing out when the check fails', async () => {
    server.use(http.get('/api/session', () => HttpResponse.json({ authenticated: true }, { status: 200 })));
    render(<CookieAuthGate />);

    expect(await screen.findByRole('alert')).toHaveTextContent('could not be checked');
    expect(screen.queryByRole('heading', { name: 'Signed in' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().epoch).toBe(0);
  });

  it('does not turn HTTP 401 or HTTP 403 into a signed-out session', async () => {
    server.use(http.get('/api/session', () => new HttpResponse(null, { status: 401 })));
    const { unmount } = render(<CookieAuthGate />);

    expect(await screen.findByText(/could not be checked/)).toBeInTheDocument();
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
    expect(sessionStore.getSnapshot().epoch).toBe(0);
    unmount();

    server.use(http.get('/api/session', () => new HttpResponse(null, { status: 403 })));
    render(<CookieAuthGate />);

    expect(await screen.findByText(/could not be checked/)).toBeInTheDocument();
    expect(sessionStore.getSnapshot().epoch).toBe(0);
  });

  it('clears the signed-in session after a confirmed local sign-out', async () => {
    const user = userEvent.setup();
    let sessionReads = 0;
    let logoutPosts = 0;
    server.use(
      http.get('/api/session', () => {
        sessionReads += 1;
        return HttpResponse.json(sessionReads === 1 ? restrictedBody() : anonymousBody('csrf-after-logout'));
      }),
      http.post('/logout', ({ request }) => {
        logoutPosts += 1;
        expect(request.headers.get('authorization')).toBeNull();
        expect(request.headers.get('x-csrf-token')).toBe('csrf-session');
        return new HttpResponse(null, { status: 204 });
      }),
    );
    queryClient.setQueryData(['invoices', 'secret'], { invoiceNumber: 'INV-SECRET' });
    render(<CookieAuthGate />);
    await screen.findByRole('heading', { name: 'Signed in' });
    const epoch = sessionStore.getSnapshot().epoch;

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.getByText(/FinOpsDesk signed out in this browser/)).toBeInTheDocument();
    expect(screen.getByText(/identity provider may still keep its own session/)).toBeInTheDocument();
    expect(logoutPosts).toBe(1);
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-after-logout');
    expect(sessionStore.getSnapshot().epoch).toBeGreaterThan(epoch);
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  });

  it('keeps the signed-in screen when sign-out is rejected', async () => {
    const user = userEvent.setup();
    let logoutPosts = 0;
    server.use(
      http.get('/api/session', () => HttpResponse.json({ ...restrictedBody(), csrfToken: 'csrf-refreshed' })),
      http.post('/logout', () => {
        logoutPosts += 1;
        return HttpResponse.json(
          { title: 'Invalid CSRF token.', code: 'auth.csrf.invalid' },
          { status: 400 },
        );
      }),
    );
    render(<CookieAuthGate />);
    await screen.findByRole('heading', { name: 'Signed in' });

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByText(/sign-out request was rejected/)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Signed in' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeEnabled();
    expect(screen.queryByText(/FinOpsDesk signed out in this browser/)).not.toBeInTheDocument();
    expect(screen.queryByText(/could not be confirmed/)).not.toBeInTheDocument();
    expect(logoutPosts).toBe(1);
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-refreshed');
  });

  it('shows the anonymous state when a lost sign-out response is followed by an anonymous session', async () => {
    const user = userEvent.setup();
    let sessionReads = 0;
    let logoutPosts = 0;
    const probe = deferred();
    server.use(
      http.get('/api/session', async () => {
        sessionReads += 1;

        if (sessionReads === 1) {
          return HttpResponse.json(restrictedBody());
        }

        await probe.promise;
        return HttpResponse.json(anonymousBody('csrf-after-lost-response'));
      }),
      http.post('/logout', () => {
        logoutPosts += 1;
        return HttpResponse.error();
      }),
    );
    queryClient.setQueryData(['invoices', 'secret'], { invoiceNumber: 'INV-SECRET' });
    render(<CookieAuthGate />);
    await screen.findByRole('heading', { name: 'Signed in' });
    const epoch = sessionStore.getSnapshot().epoch;

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByText(/Checking the browser session again/)).toBeInTheDocument();
    expect(screen.queryByText('Signed in as Ada Operator')).not.toBeInTheDocument();
    expect(screen.queryByText('INV-SECRET')).not.toBeInTheDocument();
    expect(screen.queryByText(/FinOpsDesk signed out in this browser/)).not.toBeInTheDocument();
    expect(screen.queryByText(/session was not changed/)).not.toBeInTheDocument();
    expect(screen.queryByText(/session was kept/)).not.toBeInTheDocument();
    expect(logoutPosts).toBe(1);

    probe.resolve();

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByText(/FinOpsDesk signed out in this browser/)).not.toBeInTheDocument();
    expect(screen.queryByText(/could not be confirmed/)).not.toBeInTheDocument();
    expect(logoutPosts).toBe(1);
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-after-lost-response');
    expect(sessionStore.getSnapshot().epoch).toBeGreaterThan(epoch);
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  });

  it('lets the operator retry when a lost sign-out response leaves the same session', async () => {
    const user = userEvent.setup();
    let logoutPosts = 0;
    const probe = deferred();
    server.use(
      http.get('/api/session', async ({ request }) => {
        if (request.headers.get('authorization') !== null) {
          return HttpResponse.json({ title: 'Unexpected bearer token.' }, { status: 500 });
        }

        if (logoutPosts === 0) {
          return HttpResponse.json(restrictedBody());
        }

        await probe.promise;
        return HttpResponse.json(restrictedBody());
      }),
      http.post('/logout', () => {
        logoutPosts += 1;
        return HttpResponse.error();
      }),
    );
    render(<CookieAuthGate />);
    await screen.findByRole('heading', { name: 'Signed in' });
    const epoch = sessionStore.getSnapshot().epoch;

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByText(/Checking the browser session again/)).toBeInTheDocument();
    expect(screen.queryByText(/session was not changed/)).not.toBeInTheDocument();
    probe.resolve();

    expect(await screen.findByText(unconfirmedStillSignedIn)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Signed in' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeEnabled();
    expect(screen.queryByText(/FinOpsDesk signed out in this browser/)).not.toBeInTheDocument();
    expect(screen.queryByText(/session was not changed/)).not.toBeInTheDocument();
    expect(screen.queryByText(/session was kept/)).not.toBeInTheDocument();
    expect(logoutPosts).toBe(1);
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
  });

  it('shows a session error when sign-out and the session check both fail', async () => {
    const user = userEvent.setup();
    let sessionReads = 0;
    let logoutPosts = 0;
    const probe = deferred();
    server.use(
      http.get('/api/session', async () => {
        sessionReads += 1;

        if (sessionReads === 1) {
          return HttpResponse.json(restrictedBody());
        }

        await probe.promise;
        return HttpResponse.error();
      }),
      http.post('/logout', () => {
        logoutPosts += 1;
        return new HttpResponse(null, { status: 503 });
      }),
    );
    render(<CookieAuthGate />);
    await screen.findByRole('heading', { name: 'Signed in' });
    const epoch = sessionStore.getSnapshot().epoch;

    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByText(/Checking the browser session again/)).toBeInTheDocument();
    expect(screen.queryByText('Signed in as Ada Operator')).not.toBeInTheDocument();
    expect(logoutPosts).toBe(1);
    probe.resolve();

    expect(await screen.findByText(/could not be checked/)).toBeInTheDocument();
    expect(screen.getByText(/This is not a sign-out/)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Signed in' })).not.toBeInTheDocument();
    expect(screen.queryByText('Signed in as Ada Operator')).not.toBeInTheDocument();
    expect(screen.queryByText(/FinOpsDesk signed out in this browser/)).not.toBeInTheDocument();
    expect(screen.queryByText(/session was not changed/)).not.toBeInTheDocument();
    expect(screen.queryByText(/session was kept/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument();
    expect(logoutPosts).toBe(1);
    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
  });

  it('returns to the sign-in screen when the server session is gone', async () => {
    sessionStore.replaceCookieSession(
      {
        authenticated: true,
        csrfToken: 'csrf-session',
        issuer: 'https://issuer.example',
        subject: 'oidc-operator',
        displayName: 'Ada Operator',
        expiresAt: null,
        organizations: [],
        activeOrganization: null,
        authorization: 'restricted',
        capabilities: [],
      },
      true,
    );
    queryClient.setQueryData(['invoices', 'secret'], { invoiceNumber: 'INV-SECRET' });
    server.use(http.get('/api/session', () => HttpResponse.json(anonymousBody())));
    render(<CookieAuthGate />);

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Signed in' })).not.toBeInTheDocument();
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
  });

  it('hides a previous financial recovery from a different signed-in identity', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    const descriptor = storedAllocation();
    sessionStorage.setItem(explicitAmountAttemptStorageKey, JSON.stringify(descriptor));
    server.use(
      http.get('/api/session', () =>
        HttpResponse.json({
          ...restrictedBody(),
          subject: 'other-operator',
          displayName: 'Other Operator',
        }),
      ),
    );
    render(<CookieAuthGate />);

    expect(await screen.findByText('Signed in as Other Operator')).toBeInTheDocument();
    expect(screen.queryByText(descriptor.idempotencyKey)).not.toBeInTheDocument();
    expect(screen.queryByText(descriptor.invoiceId)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Recover allocation attempt' })).not.toBeInTheDocument();
    expect(inspectAllocationAttempt(sessionStore.getSnapshot()).status).toBe('blocked-authentication');
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(JSON.stringify(descriptor));

    await expect(submitExplicitAmountAttempt()).rejects.toMatchObject({ status: 403 });
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(JSON.stringify(descriptor));
  });
});

const unconfirmedStillSignedIn =
  'Sign-out could not be confirmed. You are still signed in to FinOpsDesk in this browser. Try again when you are ready.';

function deferred() {
  let resolve: () => void = () => undefined;
  const promise = new Promise<void>((complete) => {
    resolve = () => complete();
  });
  return { promise, resolve };
}

function anonymousBody(csrfToken = 'csrf-anonymous') {
  return {
    authenticated: false,
    financialAccess: false,
    csrfToken,
    issuer: null,
    subject: null,
    displayName: null,
    expiresAt: null,
    organizations: [],
    activeOrganization: null,
  };
}

function restrictedBody() {
  return {
    authenticated: true,
    financialAccess: false,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada Operator',
    expiresAt: '2099-01-01T00:00:00.000Z',
    organizations: [],
    activeOrganization: null,
  };
}

function storedAllocation() {
  const invoiceId = '11111111-1111-1111-1111-111111111111';
  const bankTransactionId = '22222222-2222-2222-2222-222222222222';
  return {
    schemaVersion: 1,
    operation: 'ExplicitAmount',
    idempotencyKey: '99999999-9999-9999-9999-999999999999',
    invoiceId,
    bankTransactionId,
    amount: '25',
    currency: 'PLN',
    organizationId: '33333333-3333-3333-3333-333333333333',
    requestBody: `{"invoiceId":"${invoiceId}","bankTransactionId":"${bankTransactionId}","amount":{"amount":25,"currency":"PLN"}}`,
    lifecycle: 'ready',
  };
}
