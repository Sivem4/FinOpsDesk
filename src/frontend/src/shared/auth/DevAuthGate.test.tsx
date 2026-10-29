import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';

import { fakeJwt } from '../../test/jwt';
import { queryClient } from '../api/query-client';
import { sessionStore } from '../api/session-store';
import DevAuthGate from './DevAuthGate';

describe('development access token gate', () => {
  it('keeps the token out of browser storage and the URL', async () => {
    const user = userEvent.setup();
    const token = fakeJwt();
    localStorage.setItem('sentinel', '1');

    render(
      <MemoryRouter>
        <DevAuthGate>
          <p>Invoice workspace</p>
        </DevAuthGate>
      </MemoryRouter>,
    );

    await user.click(screen.getByLabelText('Development access token'));
    await user.paste(token);
    await user.click(screen.getByRole('button', { name: 'Use token' }));

    expect(await screen.findByText('Invoice workspace')).toBeInTheDocument();
    expect(localStorage.getItem('sentinel')).toBe('1');
    expect(localStorage.getItem('token')).toBeNull();
    expect(sessionStorage.length).toBe(0);
    expect(window.location.search).not.toContain(token);
    expect(document.body.textContent).not.toContain(token);

    queryClient.setQueryData(['invoices', 'visible'], { invoiceNumber: 'INV-SECRET' });
    await user.click(screen.getByRole('button', { name: 'Log out' }));

    expect(await screen.findByLabelText('Development access token')).toBeInTheDocument();
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('clears the active session when token replacement starts', async () => {
    const user = userEvent.setup();
    const token = fakeJwt();

    render(
      <MemoryRouter>
        <DevAuthGate>
          <p>Invoice workspace</p>
        </DevAuthGate>
      </MemoryRouter>,
    );

    await user.click(screen.getByLabelText('Development access token'));
    await user.paste(token);
    await user.click(screen.getByRole('button', { name: 'Use token' }));

    expect(await screen.findByText('Invoice workspace')).toBeInTheDocument();
    const activeEpoch = sessionStore.getSnapshot().epoch;
    expect(sessionStore.getSnapshot().token).toBe(token);
    queryClient.setQueryData(['invoices', 'old-session'], { invoiceNumber: 'OLD' });

    await user.click(screen.getByRole('button', { name: 'Replace token' }));

    expect(await screen.findByLabelText('Development access token')).toBeInTheDocument();
    expect(screen.queryByText('Invoice workspace')).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().token).toBeNull();
    expect(sessionStore.getSnapshot().epoch).toBeGreaterThan(activeEpoch);
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(localStorage.getItem('token')).toBeNull();
    expect(sessionStorage.length).toBe(0);
  });
});
