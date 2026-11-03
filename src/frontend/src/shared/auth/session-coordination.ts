const channelName = 'finopsdesk.session';

let channel: BroadcastChannel | null | undefined;

let localRevalidation: (() => void) | null = null;

export function registerLocalSessionRevalidation(handler: () => void): () => void {
  localRevalidation = handler;
  return () => {
    if (localRevalidation === handler) {
      localRevalidation = null;
    }
  };
}

export function requestLocalSessionRevalidation(): void {
  localRevalidation?.();
}

export function publishSessionRevalidation(): void {
  const current = sharedChannel();

  if (current === null) {
    return;
  }

  try {
    current.postMessage({ type: 'revalidate' });
  } catch {
    // A failed send does not change the local session. The next focus check asks the server.
  }
}

export function subscribeSessionRevalidation(onHint: () => void): () => void {
  const current = sharedChannel();

  if (current === null) {
    return () => undefined;
  }

  const onMessage = (event: MessageEvent<unknown>): void => {
    if (!isRevalidationHint(event.data)) {
      return;
    }

    onHint();
  };

  current.addEventListener('message', onMessage);

  return () => {
    current.removeEventListener('message', onMessage);
  };
}

export function resetSessionCoordinationForTests(): void {
  if (channel !== undefined && channel !== null) {
    channel.close();
  }

  channel = undefined;
}

function sharedChannel(): BroadcastChannel | null {
  if (channel !== undefined) {
    return channel;
  }

  if (typeof BroadcastChannel === 'undefined') {
    channel = null;
    return null;
  }

  try {
    channel = new BroadcastChannel(channelName);
    return channel;
  } catch {
    channel = null;
    return null;
  }
}

function isRevalidationHint(value: unknown): boolean {
  return typeof value === 'object'
    && value !== null
    && !Array.isArray(value)
    && (value as { type?: unknown }).type === 'revalidate';
}
