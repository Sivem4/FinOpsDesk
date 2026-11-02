import { describe, expect, it, vi } from 'vitest';

import { publishSessionRevalidation, subscribeSessionRevalidation } from './session-coordination';

describe('session coordination', () => {
  it('sends only a revalidation hint and ignores that hint locally', async () => {
    const hints: unknown[] = [];
    const stop = subscribeSessionRevalidation(() => {
      hints.push('local');
    });
    const channel = new BroadcastChannel('finopsdesk.session');
    channel.addEventListener('message', (event: MessageEvent<unknown>) => {
      hints.push(event.data);
    });

    publishSessionRevalidation();

    await vi.waitFor(() => expect(hints).toEqual([{ type: 'revalidate' }]));
    stop();
    channel.close();
  });

  it('accepts a hint from another channel and ignores other messages', async () => {
    const onHint = vi.fn();
    const stop = subscribeSessionRevalidation(onHint);
    const channel = new BroadcastChannel('finopsdesk.session');

    channel.postMessage({ type: 'revalidate', organizationId: 'secret' });
    channel.postMessage({ type: 'other' });

    await vi.waitFor(() => expect(onHint).toHaveBeenCalledTimes(1));
    stop();
    channel.close();
  });
});
