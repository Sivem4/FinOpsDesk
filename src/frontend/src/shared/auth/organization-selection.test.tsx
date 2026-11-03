import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';

import {
  explicitAmountAttemptStorageKey,
  inspectAllocationAttempt,
  submitExplicitAmountAttempt,
} from '../../features/reconciliation/allocation-attempt';
import { reversalAttemptStorageKey } from '../../features/allocations/reversal-attempt';
import { queryClient } from '../api/query-client';
import { sessionStore } from '../api/session-store';
import { server } from '../../test/server';
import { CookieAuthGate } from './CookieAuthGate';
import { subscribeSessionRevalidation } from './session-coordination';

const northId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const southId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const westId = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
const eastId = 'dddddddd-dddd-dddd-dddd-dddddddddddd';

describe('organization selection', () => {
  it('requires an explicit choice when one membership exists', async () => {
    server.use(http.get('/api/session', () => HttpResponse.json(sessionBody({
      organizations: [organization('North Ledger', northId)],
    }))));
    render(<CookieAuthGate />);

    expect(await screen.findByRole('button', { name: 'Select North Ledger' })).toBeInTheDocument();
    expect(screen.queryByText('Active organization: North Ledger')).not.toBeInTheDocument();
  });

  it('shows every server membership and does not invent a selection', async () => {
    server.use(http.get('/api/session', () => HttpResponse.json(sessionBody({
      organizations: [organization('North Ledger', northId), organization('South Ledger', southId)],
    }))));
    render(<CookieAuthGate />);

    expect(await screen.findByRole('button', { name: 'Select North Ledger' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Select South Ledger' })).toBeInTheDocument();
    expect(screen.queryByText(/Active organization:/)).not.toBeInTheDocument();
  });

  it('accepts the active organization only from the follow-up session read', async () => {
    const user = userEvent.setup();
    const followUp = deferred<void>();
    let posts = 0;
    let authorization: string | null = null;
    let contentType: string | null = null;
    let csrf: string | null = null;
    let body = '';
    server.use(
      http.get('/api/session', () => {
        if (posts === 0) {
          return HttpResponse.json(sessionBody({
            organizations: [organization('North Ledger', northId), organization('South Ledger', southId)],
          }));
        }

        return followUp.promise.then(() => HttpResponse.json(sessionBody({
          organizations: [organization('North Ledger', northId), organization('South Ledger', southId)],
          activeOrganization: organization('North Ledger', northId),
        })));
      }),
      http.post('/api/session/organization', async ({ request }) => {
        posts += 1;
        authorization = request.headers.get('authorization');
        contentType = request.headers.get('content-type');
        csrf = request.headers.get('x-csrf-token');
        body = await request.text();
        return new HttpResponse(null, { status: 204 });
      }),
      http.get('/api/invoices', () => HttpResponse.json([])),
    );
    render(<CookieAuthGate />);
    await user.click(await screen.findByRole('button', { name: 'Select North Ledger' }));

    expect(await screen.findByText('Checking the organization.')).toBeInTheDocument();
    expect(screen.queryByText('Active organization: North Ledger')).not.toBeInTheDocument();
    expect(posts).toBe(1);
    expect(authorization).toBeNull();
    expect(contentType).toContain('application/json');
    expect(csrf).toBe('csrf-session');
    expect(JSON.parse(body)).toEqual({ organizationId: northId });

    followUp.resolve();
    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Switch to South Ledger' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Invoices' })).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().financialAccess).toBe(false);
  });

  it('does not repeat an organization request when the response is lost', async () => {
    const user = userEvent.setup();
    const followUp = deferred<void>();
    let posts = 0;
    server.use(
      http.get('/api/session', () => {
        if (posts === 0) {
          return HttpResponse.json(sessionBody({
            organizations: [organization('North Ledger', northId)],
          }));
        }

        return followUp.promise.then(() => HttpResponse.json(sessionBody({
          organizations: [organization('North Ledger', northId)],
          activeOrganization: organization('North Ledger', northId),
        })));
      }),
      http.post('/api/session/organization', () => {
        posts += 1;
        return HttpResponse.error();
      }),
    );
    render(<CookieAuthGate />);
    await user.click(await screen.findByRole('button', { name: 'Select North Ledger' }));

    expect(await screen.findByText('Checking the organization.')).toBeInTheDocument();
    expect(screen.queryByText(/definitely failed/)).not.toBeInTheDocument();
    followUp.resolve();
    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    expect(posts).toBe(1);
  });

  it('shows a session error when the follow-up read fails', async () => {
    const user = userEvent.setup();
    let reads = 0;
    server.use(
      http.get('/api/session', () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json(sessionBody({
            organizations: [organization('North Ledger', northId)],
          }));
        }

        return HttpResponse.error();
      }),
      http.post('/api/session/organization', () => new HttpResponse(null, { status: 204 })),
    );
    render(<CookieAuthGate />);
    await user.click(await screen.findByRole('button', { name: 'Select North Ledger' }));

    expect(await screen.findByText(/could not be checked/)).toBeInTheDocument();
    expect(screen.queryByText('Active organization: North Ledger')).not.toBeInTheDocument();
  });

  it('revalidates a forbidden organization without signing out', async () => {
    const user = userEvent.setup();
    let reads = 0;
    server.use(
      http.get('/api/session', () => {
        reads += 1;
        return HttpResponse.json(sessionBody({
          organizations: reads === 1
            ? [organization('North Ledger', northId), organization('South Ledger', southId)]
            : [organization('South Ledger', southId)],
          activeOrganization: null,
        }));
      }),
      http.post('/api/session/organization', () => HttpResponse.json(
        { code: 'auth.organization.forbidden' },
        { status: 403 },
      )),
    );
    render(<CookieAuthGate />);
    await user.click(await screen.findByRole('button', { name: 'Select North Ledger' }));

    expect(await screen.findByText('That organization is not available.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Signed in' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Select South Ledger' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Select North Ledger' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument();
  });

  it('refreshes a rejected CSRF token and waits for another click', async () => {
    const user = userEvent.setup();
    let posts = 0;
    server.use(
      http.get('/api/session', () => HttpResponse.json(sessionBody({
        csrfToken: posts === 0 ? 'csrf-session' : 'csrf-refreshed',
        organizations: [organization('North Ledger', northId)],
      }))),
      http.post('/api/session/organization', () => {
        posts += 1;
        return HttpResponse.json({ code: 'auth.csrf.invalid' }, { status: 400 });
      }),
    );
    render(<CookieAuthGate />);
    await user.click(await screen.findByRole('button', { name: 'Select North Ledger' }));

    expect(await screen.findByText(/organization request was rejected/)).toBeInTheDocument();
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-refreshed');
    expect(posts).toBe(1);
    expect(screen.getByRole('button', { name: 'Select North Ledger' })).toBeEnabled();
  });

  it('discards a late session read after a newer read starts', async () => {
    const staleRead = deferred<void>();
    const freshRead = deferred<void>();
    let reads = 0;
    server.use(
      http.get('/api/session', () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json(sessionBody({
            organizations: [organization('North Ledger', northId), organization('South Ledger', southId)],
          }));
        }

        if (reads === 2) {
          return staleRead.promise.then(() => HttpResponse.json(sessionBody({
            organizations: [organization('South Ledger', southId)],
            activeOrganization: organization('South Ledger', southId),
          })));
        }

        return freshRead.promise.then(() => HttpResponse.json(sessionBody({
          organizations: [organization('North Ledger', northId)],
          activeOrganization: organization('North Ledger', northId),
        })));
      }),
    );
    render(<CookieAuthGate />);
    expect(await screen.findByRole('button', { name: 'Select North Ledger' })).toBeInTheDocument();
    publishFromAnotherTab();
    await vi.waitFor(() => expect(reads).toBe(2));
    publishFromAnotherTab();
    staleRead.resolve();
    await vi.waitFor(() => expect(reads).toBe(3));
    freshRead.resolve();

    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    expect(screen.queryByText('Active organization: South Ledger')).not.toBeInTheDocument();
  });

  it('discards a late session failure after a newer read starts', async () => {
    const staleRead = deferred<void>();
    const freshRead = deferred<void>();
    let reads = 0;
    server.use(
      http.get('/api/session', () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json(sessionBody({
            organizations: [organization('North Ledger', northId)],
          }));
        }

        if (reads === 2) {
          return staleRead.promise.then(() => HttpResponse.error());
        }

        return freshRead.promise.then(() => HttpResponse.json(sessionBody({
          organizations: [organization('North Ledger', northId)],
          activeOrganization: organization('North Ledger', northId),
        })));
      }),
    );
    render(<CookieAuthGate />);
    expect(await screen.findByRole('button', { name: 'Select North Ledger' })).toBeInTheDocument();
    publishFromAnotherTab();
    await vi.waitFor(() => expect(reads).toBe(2));
    publishFromAnotherTab();
    staleRead.resolve();
    await vi.waitFor(() => expect(reads).toBe(3));
    freshRead.resolve();

    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    expect(screen.queryByText(/could not be checked/)).not.toBeInTheDocument();
  });

  it('does not let an older organization response replace a newer sign-out', async () => {
    const user = userEvent.setup();
    const post = deferred<void>();
    let organizationPosts = 0;
    let signedOut = false;
    server.use(
      http.get('/api/session', () => HttpResponse.json(signedOut
        ? anonymousBody()
        : sessionBody({
          organizations: [organization('North Ledger', northId)],
          activeOrganization: organizationPosts === 0 ? null : organization('North Ledger', northId),
        }))),
      http.post('/api/session/organization', () => {
        organizationPosts += 1;
        return post.promise.then(() => new HttpResponse(null, { status: 204 }));
      }),
      http.post('/logout', () => {
        signedOut = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    render(<CookieAuthGate />);
    await user.click(await screen.findByRole('button', { name: 'Select North Ledger' }));
    expect(await screen.findByText('Checking the organization.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Sign out' }));
    post.resolve();

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByText('Active organization: North Ledger')).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
  });

  it('asks the server again after another tab changes the cookie', async () => {
    const seen = deferred<void>();
    let reads = 0;
    server.use(http.get('/api/session', () => {
      reads += 1;
      if (reads === 1) {
        return HttpResponse.json(sessionBody({
          organizations: [organization('North Ledger', northId), organization('South Ledger', southId)],
        }));
      }

      seen.resolve();
      return HttpResponse.json(sessionBody({
        organizations: [organization('North Ledger', northId), organization('South Ledger', southId)],
        activeOrganization: organization('South Ledger', southId),
      }));
    }));
    render(<CookieAuthGate />);
    expect(await screen.findByRole('button', { name: 'Select North Ledger' })).toBeInTheDocument();
    publishFromAnotherTab();
    await seen.promise;

    expect(await screen.findByText('Active organization: South Ledger')).toBeInTheDocument();
    expect(reads).toBe(2);
  });

  it('checks the session once when focus and visibility return together', async () => {
    let reads = 0;
    server.use(http.get('/api/session', () => {
      reads += 1;
      return HttpResponse.json(sessionBody({
        organizations: [organization('North Ledger', northId)],
        activeOrganization: reads === 1 ? null : organization('North Ledger', northId),
      }));
    }));
    render(<CookieAuthGate />);
    expect(await screen.findByRole('button', { name: 'Select North Ledger' })).toBeInTheDocument();
    window.dispatchEvent(new Event('blur'));
    window.dispatchEvent(new Event('focus'));
    document.dispatchEvent(new Event('visibilitychange'));

    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    expect(reads).toBe(2);
  });

  it('keeps recovery descriptors blocked for every cookie identity', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    const allocation = storedAllocation(northId);
    const reversal = storedReversal(northId);
    sessionStorage.setItem(explicitAmountAttemptStorageKey, JSON.stringify(allocation));
    sessionStorage.setItem(reversalAttemptStorageKey, JSON.stringify(reversal));
    let financialPosts = 0;
    server.use(
      http.post('/api/allocations', () => {
        financialPosts += 1;
        return new HttpResponse(null, { status: 204 });
      }),
      http.post('/api/allocations/:allocationId/reversal', () => {
        financialPosts += 1;
        return new HttpResponse(null, { status: 204 });
      }),
      http.get('/api/session', () => HttpResponse.json(sessionBody({
        organizations: [organization('North Ledger', northId)],
        activeOrganization: organization('North Ledger', northId),
      }))),
    );
    const view = render(<CookieAuthGate />);

    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    expect(inspectAllocationAttempt(sessionStore.getSnapshot()).status).toBe('blocked-authentication');
    await expect(submitExplicitAmountAttempt()).rejects.toMatchObject({ status: 403 });
    expect(financialPosts).toBe(0);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(JSON.stringify(allocation));

    server.use(http.get('/api/session', () => HttpResponse.json(sessionBody({
      subject: 'other-operator',
      displayName: 'Other Operator',
      organizations: [organization('North Ledger', northId)],
      activeOrganization: organization('North Ledger', northId),
    }))));
    window.dispatchEvent(new Event('blur'));
    window.dispatchEvent(new Event('focus'));
    expect(await screen.findByText('Signed in as Other Operator')).toBeInTheDocument();
    queryClient.setQueryData(['invoices', 'secret'], { invoiceNumber: 'INV-SECRET' });
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(JSON.stringify(allocation));
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBe(JSON.stringify(reversal));

    server.use(http.get('/api/session', () => HttpResponse.json(sessionBody({
      organizations: [organization('South Ledger', southId)],
      activeOrganization: organization('South Ledger', southId),
    }))));
    window.dispatchEvent(new Event('blur'));
    window.dispatchEvent(new Event('focus'));
    expect(await screen.findByText('Active organization: South Ledger')).toBeInTheDocument();
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(JSON.stringify(allocation));

    server.use(
      http.post('/logout', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/session', () => HttpResponse.json(anonymousBody())),
    );
    view.rerender(<CookieAuthGate />);
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(JSON.stringify(allocation));
    expect(financialPosts).toBe(0);
  });

  it('explains an authenticated session that has no membership', async () => {
    server.use(http.get('/api/session', () => HttpResponse.json(sessionBody())));
    render(<CookieAuthGate />);

    expect(await screen.findByRole('heading', { name: 'Signed in' })).toBeInTheDocument();
    expect(screen.getByText(/no organization membership is available/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Select / })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
  });

  it('shows the sign-in form when the server confirms the session is gone', async () => {
    const user = userEvent.setup();
    let reads = 0;
    server.use(
      http.get('/api/session', () => {
        reads += 1;
        return HttpResponse.json(reads === 1
          ? sessionBody({ organizations: [organization('North Ledger', northId)] })
          : anonymousBody());
      }),
      http.post('/api/session/organization', () => new HttpResponse(null, { status: 401 })),
    );
    render(<CookieAuthGate />);
    await user.click(await screen.findByRole('button', { name: 'Select North Ledger' }));

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByText('Active organization: North Ledger')).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
  });

  it('drops a revoked active organization after the server list changes', async () => {
    let reads = 0;
    server.use(http.get('/api/session', () => {
      reads += 1;
      if (reads === 1) {
        return HttpResponse.json(sessionBody({
          organizations: [organization('North Ledger', northId), organization('South Ledger', southId)],
          activeOrganization: organization('North Ledger', northId),
        }));
      }

      return HttpResponse.json(sessionBody({
        organizations: [organization('South Ledger', southId)],
        activeOrganization: null,
      }));
    }));
    render(<CookieAuthGate />);
    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    window.dispatchEvent(new Event('blur'));
    window.dispatchEvent(new Event('focus'));

    expect(await screen.findByRole('button', { name: 'Select South Ledger' })).toBeInTheDocument();
    expect(screen.queryByText('Active organization: North Ledger')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /North Ledger/ })).not.toBeInTheDocument();
  });

  it('ignores an organization response after a newer identity is verified', async () => {
    const user = userEvent.setup();
    const post = deferred<void>();
    let posts = 0;
    let reads = 0;
    server.use(
      http.get('/api/session', () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json(sessionBody({
            organizations: [organization('North Ledger', northId)],
          }));
        }

        return HttpResponse.json(sessionBody({
          subject: 'other-operator',
          displayName: 'Other Operator',
          organizations: [organization('South Ledger', southId)],
          activeOrganization: null,
        }));
      }),
      http.post('/api/session/organization', () => {
        posts += 1;
        return post.promise.then(() => new HttpResponse(null, { status: 204 }));
      }),
    );
    render(<CookieAuthGate />);
    await user.click(await screen.findByRole('button', { name: 'Select North Ledger' }));
    expect(await screen.findByText('Checking the organization.')).toBeInTheDocument();
    publishFromAnotherTab();
    expect(screen.queryByText('Signed in as Other Operator')).not.toBeInTheDocument();
    post.resolve();

    expect(await screen.findByText('Signed in as Other Operator')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Select South Ledger' })).toBeInTheDocument();
    expect(screen.queryByText('Active organization: North Ledger')).not.toBeInTheDocument();
    expect(posts).toBe(1);
    expect(reads).toBe(2);
  });

  it('publishes a revalidation hint before the uncertain follow-up read finishes', async () => {
    const user = userEvent.setup();
    const followUp = deferred<void>();
    const hint = deferred<unknown>();
    const channel = new BroadcastChannel('finopsdesk.session');
    channel.addEventListener('message', (event: MessageEvent<unknown>) => {
      hint.resolve(event.data);
    });
    let reads = 0;
    server.use(
      http.get('/api/session', () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json(sessionBody({
            organizations: [organization('North Ledger', northId)],
          }));
        }

        return followUp.promise.then(() => HttpResponse.json(sessionBody({
          organizations: [organization('North Ledger', northId)],
        })));
      }),
      http.post('/api/session/organization', () => HttpResponse.error()),
    );
    render(<CookieAuthGate />);
    await user.click(await screen.findByRole('button', { name: 'Select North Ledger' }));

    await expect(hint.promise).resolves.toEqual({ type: 'revalidate' });
    expect(screen.getByText('Checking the organization.')).toBeInTheDocument();
    followUp.resolve();
    expect(await screen.findByRole('button', { name: 'Select North Ledger' })).toBeInTheDocument();
    channel.close();
  });

  it('coalesces rapid revalidation hints into one follow-up read', async () => {
    const gate = deferred<void>();
    let reads = 0;
    server.use(http.get('/api/session', () => {
      reads += 1;
      if (reads === 1) {
        return HttpResponse.json(sessionBody({
          organizations: [organization('North Ledger', northId)],
        }));
      }

      if (reads === 2) {
        return gate.promise.then(() => HttpResponse.json(sessionBody({
          organizations: [organization('North Ledger', northId)],
        })));
      }

      return HttpResponse.json(sessionBody({
        organizations: [organization('North Ledger', northId)],
        activeOrganization: organization('North Ledger', northId),
      }));
    }));
    render(<CookieAuthGate />);
    expect(await screen.findByRole('button', { name: 'Select North Ledger' })).toBeInTheDocument();
    publishFromAnotherTab();
    publishFromAnotherTab();
    publishFromAnotherTab();
    await vi.waitFor(() => expect(reads).toBe(2));
    gate.resolve();

    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    expect(reads).toBe(3);
  });

  it('reads the server again after focus arrives during organization selection', async () => {
    const user = userEvent.setup();
    const post = deferred<void>();
    let posts = 0;
    let confirmed = false;
    server.use(
      http.get('/api/session', () => HttpResponse.json(memberships(confirmed))),
      http.post('/api/session/organization', () => {
        posts += 1;
        return post.promise.then(() => {
          confirmed = true;
          return new HttpResponse(null, { status: 204 });
        });
      }),
    );
    render(<CookieAuthGate />);
    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    queryClient.setQueryData(['invoices', 'secret'], { invoiceNumber: 'INV-SECRET' });
    const epoch = sessionStore.getSnapshot().epoch;
    await user.click(screen.getByRole('button', { name: 'Switch to South Ledger' }));
    expect(await screen.findByText('Checking the organization.')).toBeInTheDocument();
    window.dispatchEvent(new Event('blur'));
    window.dispatchEvent(new Event('focus'));
    await vi.waitFor(() => expect(posts).toBe(1));
    expect(screen.queryByText('Active organization: South Ledger')).not.toBeInTheDocument();
    post.resolve();

    expect(await screen.findByText('Active organization: South Ledger')).toBeInTheDocument();
    expect(sessionStore.getSnapshot().activeOrganization).toEqual(organization('South Ledger', southId));
    expect(sessionStore.getSnapshot().epoch).toBe(epoch + 1);
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(posts).toBe(1);
  });

  it('reads the server again after another tab signals during organization selection', async () => {
    const user = userEvent.setup();
    const post = deferred<void>();
    let posts = 0;
    let confirmed = false;
    let reads = 0;
    server.use(
      http.get('/api/session', () => {
        reads += 1;
        return HttpResponse.json(memberships(confirmed));
      }),
      http.post('/api/session/organization', () => {
        posts += 1;
        return post.promise.then(() => {
          confirmed = true;
          return new HttpResponse(null, { status: 204 });
        });
      }),
    );
    render(<CookieAuthGate />);
    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    const readsBeforeHint = reads;
    await user.click(screen.getByRole('button', { name: 'Switch to South Ledger' }));
    expect(await screen.findByText('Checking the organization.')).toBeInTheDocument();
    publishFromAnotherTab();
    await vi.waitFor(() => expect(posts).toBe(1));
    expect(reads).toBe(readsBeforeHint);
    post.resolve();

    expect(await screen.findByText('Active organization: South Ledger')).toBeInTheDocument();
    expect(sessionStore.getSnapshot().activeOrganization?.organizationId).toBe(southId);
    expect(posts).toBe(1);
  });

  it('keeps an uncertain organization result when another tab asks during the lost response', async () => {
    const user = userEvent.setup();
    const post = deferred<void>();
    let posts = 0;
    let confirmed = false;
    server.use(
      http.get('/api/session', () => HttpResponse.json(memberships(confirmed))),
      http.post('/api/session/organization', () => {
        posts += 1;
        return post.promise.then(() => {
          confirmed = true;
          return HttpResponse.error();
        });
      }),
    );
    render(<CookieAuthGate />);
    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Switch to South Ledger' }));
    expect(await screen.findByText('Checking the organization.')).toBeInTheDocument();
    publishFromAnotherTab();
    post.resolve();

    expect(await screen.findByText('Active organization: South Ledger')).toBeInTheDocument();
    expect(posts).toBe(1);
    expect(screen.queryByText(/definitely failed/)).not.toBeInTheDocument();
  });

  it('revalidates again after a superseded hint read resolves late', async () => {
    const obsolete = deferred<void>();
    const followUp = deferred<void>();
    let reads = 0;
    server.use(http.get('/api/session', () => {
      reads += 1;
      if (reads === 1) {
        return HttpResponse.json(memberships(false));
      }

      if (reads === 2) {
        return obsolete.promise.then(() => HttpResponse.json(sessionBody({
          organizations: [organization('West Ledger', westId)],
          activeOrganization: organization('West Ledger', westId),
        })));
      }

      if (reads === 3) {
        return followUp.promise.then(() => HttpResponse.json(memberships(true)));
      }

      return HttpResponse.json(sessionBody({
        organizations: [organization('East Ledger', eastId)],
        activeOrganization: organization('East Ledger', eastId),
      }));
    }));
    render(<CookieAuthGate />);
    expect(await screen.findByText('Active organization: North Ledger')).toBeInTheDocument();
    publishFromAnotherTab();
    await vi.waitFor(() => expect(reads).toBe(2));
    await deliverRevalidation();
    obsolete.resolve();
    await vi.waitFor(() => expect(reads).toBe(3));
    expect(screen.queryByText('Active organization: West Ledger')).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().activeOrganization?.organizationId).toBe(northId);
    publishFromAnotherTab();
    followUp.resolve();

    expect(await screen.findByText('Active organization: East Ledger')).toBeInTheDocument();
    expect(sessionStore.getSnapshot().activeOrganization?.organizationId).toBe(eastId);
  });
});

function publishFromAnotherTab(): void {
  const channel = new BroadcastChannel('finopsdesk.session');
  channel.postMessage({ type: 'revalidate' });
  channel.close();
}

async function deliverRevalidation(): Promise<void> {
  const delivered = deferred<void>();
  const unsubscribe = subscribeSessionRevalidation(() => delivered.resolve());
  publishFromAnotherTab();
  await delivered.promise;
  unsubscribe();
}

function deferred<T>() {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

function organization(displayName: string, organizationId: string) {
  return { organizationId, displayName };
}

function memberships(confirmed: boolean) {
  return sessionBody({
    organizations: [organization('North Ledger', northId), organization('South Ledger', southId)],
    activeOrganization: confirmed ? organization('South Ledger', southId) : organization('North Ledger', northId),
  });
}

function anonymousBody() {
  return {
    authenticated: false,
    financialAccess: false,
    csrfToken: 'csrf-anonymous',
    issuer: null,
    subject: null,
    displayName: null,
    expiresAt: null,
    organizations: [],
    activeOrganization: null,
  };
}

function sessionBody(overrides: Record<string, unknown> = {}) {
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
    ...overrides,
  };
}

function storedAllocation(organizationId: string) {
  return {
    schemaVersion: 1,
    operation: 'ExplicitAmount',
    idempotencyKey: '99999999-9999-9999-9999-999999999999',
    invoiceId: '11111111-1111-1111-1111-111111111111',
    bankTransactionId: '22222222-2222-2222-2222-222222222222',
    amount: '25',
    currency: 'PLN',
    organizationId,
    requestBody: '{"invoiceId":"11111111-1111-1111-1111-111111111111","bankTransactionId":"22222222-2222-2222-2222-222222222222","amount":{"amount":25,"currency":"PLN"}}',
    lifecycle: 'uncertain',
  };
}

function storedReversal(organizationId: string) {
  return {
    schemaVersion: 1,
    operation: 'Reversal',
    idempotencyKey: '88888888-8888-8888-8888-888888888888',
    allocationId: '77777777-7777-7777-7777-777777777777',
    organizationId,
    requestBody: '{"allocationId":"77777777-7777-7777-7777-777777777777"}',
    lifecycle: 'uncertain',
  };
}
