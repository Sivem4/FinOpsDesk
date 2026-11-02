import { Alert, Button, Container, Stack, Typography } from '@mui/material';
import { useEffect, useRef, useState } from 'react';

import { isAbortError } from '../api/errors';
import { sessionStore } from '../api/session-store';
import {
  beginSessionRead,
  commitBrowserSession,
  isCurrentSessionRead,
  loadBrowserSession,
  readProblemCode,
  type BrowserSessionLoad,
} from './browser-session';
import { enqueueCookieAction } from './cookie-actions';
import { OrganizationSelector } from './OrganizationSelector';
import { publishSessionRevalidation, subscribeSessionRevalidation } from './session-coordination';
import { useSession } from './session';

type Phase = 'initializing' | 'anonymous' | 'restricted' | 'reconciling' | 'error';

type GateLife = {
  alive: boolean;
  mutation: boolean;
  hintOwner: number | null;
  owed: boolean;
};

type PhaseRef = { current: Phase };

type SetPhase = (phase: Phase) => void;

type SetGeneration = (update: (value: number) => number) => void;

type SetMessage = (message: string | null) => void;

const unconfirmedSignOutMessage =
  'Sign-out could not be confirmed. You are still signed in to FinOpsDesk in this browser. Try again when you are ready.';

export function CookieAuthGate() {
  const session = useSession();
  const [phase, setPhase] = useState<Phase>('initializing');
  const [generation, setGeneration] = useState(0);
  const [logoutError, setLogoutError] = useState<string | null>(null);
  const [selectionMessage, setSelectionMessage] = useState<string | null>(null);
  const [localSignOut, setLocalSignOut] = useState(false);
  const [pending, setPending] = useState(false);
  const [logoutUncertain, setLogoutUncertain] = useState(false);
  const logoutCheckRef = useRef(false);
  const phaseRef = useRef(phase);
  const life = useRef<GateLife>({
    alive: true,
    mutation: false,
    hintOwner: null,
    owed: false,
  });

  useEffect(() => {
    phaseRef.current = phase;
  }, [phase]);

  useEffect(() => {
    const current = life.current;
    current.alive = true;
    return () => {
      current.alive = false;
      current.mutation = false;
      current.hintOwner = null;
      current.owed = false;
    };
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    let cancelled = false;
    const readId = beginSessionRead();
    const observedEpoch = sessionStore.getSnapshot().epoch;

    if (life.current.hintOwner === -1) {
      life.current.hintOwner = readId;
    }

    void loadBrowserSession(controller.signal)
      .then((result) => {
        if (cancelled || !life.current.alive || !canApply(readId, observedEpoch)) {
          return;
        }

        applyLoadedSession(
          result,
          observedEpoch,
          readId,
          logoutCheckRef,
          setLogoutUncertain,
          setLogoutError,
          setLocalSignOut,
          setPhase,
        );
      })
      .catch((error: unknown) => {
        if (cancelled || !life.current.alive || isAbortError(error) || !canApply(readId, observedEpoch)) {
          return;
        }

        finishUnconfirmedSignOut(logoutCheckRef, setLogoutUncertain, setLogoutError, false);
        setPhase('error');
      })
      .finally(() => {
        if (!life.current.alive) {
          return;
        }

        settleRead(life.current, readId, phaseRef, setPhase, setGeneration, setSelectionMessage);
      });

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [generation]);

  useEffect(() => {
    let away = false;
    let queued = false;
    const markAway = (): void => {
      away = true;
    };
    const schedule = (): void => {
      if (!away || document.visibilityState === 'hidden' || phaseRef.current === 'initializing' || queued) {
        return;
      }

      away = false;
      queued = true;
      queueMicrotask(() => {
        queued = false;

        if (phaseRef.current === 'initializing') {
          return;
        }

        requestRevalidation(life.current, phaseRef, setPhase, setGeneration, setSelectionMessage);
      });
    };
    const onVisibility = (): void => {
      if (document.visibilityState === 'hidden') {
        markAway();
        return;
      }

      schedule();
    };

    window.addEventListener('blur', markAway);
    window.addEventListener('focus', schedule);
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      window.removeEventListener('blur', markAway);
      window.removeEventListener('focus', schedule);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, []);

  useEffect(() => {
    return subscribeSessionRevalidation(() => {
      requestRevalidation(life.current, phaseRef, setPhase, setGeneration, setSelectionMessage);
    });
  }, []);

  if (phase === 'initializing') {
    return (
      <Container maxWidth="sm" sx={{ py: 8 }}>
        <Typography variant="h4" component="h1" gutterBottom>
          FinOpsDesk
        </Typography>
        <Typography role="status">
          {logoutUncertain
            ? 'Sign-out could not be confirmed. Checking the browser session again.'
            : 'Checking the browser session'}
        </Typography>
      </Container>
    );
  }

  if (phase === 'reconciling') {
    return (
      <Container maxWidth="sm" sx={{ py: 8 }}>
        <Stack spacing={2}>
          <Typography variant="h4" component="h1" gutterBottom>
            FinOpsDesk
          </Typography>
          <Typography role="status">Checking the organization.</Typography>
          <Button
            type="button"
            variant="outlined"
            onClick={() => {
              void enqueueCookieAction(() => signOut(
                life.current,
                setPending,
                setLogoutError,
                setLocalSignOut,
                setPhase,
                setGeneration,
                setLogoutUncertain,
                logoutCheckRef,
              ));
            }}
          >
            Sign out
          </Button>
        </Stack>
      </Container>
    );
  }

  if (phase === 'error') {
    return (
      <Container maxWidth="sm" sx={{ py: 8 }}>
        <Stack spacing={2}>
          <Typography variant="h4" component="h1">
            FinOpsDesk
          </Typography>
          <Alert severity="error">
            The browser session could not be checked. Financial records stay hidden. This is not a sign-out.
          </Alert>
          <Button
            type="button"
            variant="outlined"
            onClick={() => {
              setLogoutError(null);
              setSelectionMessage(null);
              setPhase('initializing');
              setGeneration((value) => value + 1);
            }}
          >
            Try again
          </Button>
        </Stack>
      </Container>
    );
  }

  if (phase === 'anonymous') {
    if (session.csrfToken === null) {
      return (
        <Container maxWidth="sm" sx={{ py: 8 }}>
          <Alert severity="error">The browser session did not include a sign-in token.</Alert>
        </Container>
      );
    }

    return (
      <Container maxWidth="sm" sx={{ py: 8 }}>
        <Stack spacing={2}>
          <Typography variant="h4" component="h1">
            FinOpsDesk
          </Typography>
          {localSignOut ? (
            <Alert severity="success">
              FinOpsDesk signed out in this browser. The identity provider may still keep its own session.
            </Alert>
          ) : (
            <Typography>
              Sign in to FinOpsDesk. This browser does not receive an access token.
            </Typography>
          )}
          <form aria-label="Sign in" action="/login" method="POST">
            <input type="hidden" name="__RequestVerificationToken" value={session.csrfToken} />
            <input type="hidden" name="returnUrl" value="/" />
            <Button type="submit" variant="contained">
              Sign in
            </Button>
          </form>
        </Stack>
      </Container>
    );
  }

  return (
    <Container maxWidth="sm" sx={{ py: 8 }}>
      <Stack spacing={2}>
        <Typography variant="h4" component="h1">
          Signed in
        </Typography>
        <Alert severity="info">
          Authentication succeeded. Financial access is not available yet.
        </Alert>
        {session.displayName === null ? null : (
          <Typography>Signed in as {session.displayName}</Typography>
        )}
        <OrganizationSelector
          organizations={session.organizations}
          activeOrganization={session.activeOrganization}
          disabled={pending}
          onSelect={(organizationId) => {
            setSelectionMessage(null);
            void enqueueCookieAction(() => selectOrganization(
              life.current,
              organizationId,
              setPending,
              setSelectionMessage,
              setPhase,
              setGeneration,
              phaseRef,
              setLocalSignOut,
              setLogoutUncertain,
              setLogoutError,
              logoutCheckRef,
            ));
          }}
        />
        <Typography>
          Sign out ends the FinOpsDesk session in this browser. The identity provider may keep its own session.
        </Typography>
        {selectionMessage === null ? null : <Alert severity="warning">{selectionMessage}</Alert>}
        {logoutError === null ? null : <Alert severity="warning">{logoutError}</Alert>}
        <Button
          type="button"
          variant="outlined"
          onClick={() => {
            void enqueueCookieAction(() => signOut(
              life.current,
              setPending,
              setLogoutError,
              setLocalSignOut,
              setPhase,
              setGeneration,
              setLogoutUncertain,
              logoutCheckRef,
            ));
          }}
        >
          Sign out
        </Button>
      </Stack>
    </Container>
  );
}

function requestRevalidation(
  life: GateLife,
  phaseRef: PhaseRef,
  setPhase: SetPhase,
  setGeneration: SetGeneration,
  setSelectionMessage: SetMessage,
): void {
  if (!life.alive) {
    return;
  }

  if (life.mutation) {
    life.owed = true;
    return;
  }

  if (life.hintOwner !== null) {
    life.owed = true;
    beginSessionRead();
    return;
  }

  beginSessionRead();
  life.owed = false;
  life.hintOwner = -1;
  setSelectionMessage(null);

  if (phaseRef.current !== 'initializing') {
    setPhase('reconciling');
  }

  setGeneration((value) => value + 1);
}

function settleRead(
  life: GateLife,
  readId: number,
  phaseRef: PhaseRef,
  setPhase: SetPhase,
  setGeneration: SetGeneration,
  setSelectionMessage: SetMessage,
): void {
  const owned = life.hintOwner === readId;

  if (!isCurrentSessionRead(readId)) {
    if (!owned) {
      return;
    }

    life.hintOwner = null;

    if (life.mutation) {
      life.owed = true;
      return;
    }

    if (life.owed) {
      life.owed = false;
      requestRevalidation(life, phaseRef, setPhase, setGeneration, setSelectionMessage);
    }

    return;
  }

  if (owned) {
    life.hintOwner = null;
  }

  if (life.mutation || !life.owed) {
    return;
  }

  life.owed = false;
  requestRevalidation(life, phaseRef, setPhase, setGeneration, setSelectionMessage);
}

function claimSessionRefresh(life: GateLife, setPhase: SetPhase, setGeneration: SetGeneration, phase: Phase | null): void {
  if (!life.alive) {
    return;
  }

  life.owed = false;
  life.hintOwner = -1;

  if (phase !== null) {
    setPhase(phase);
  }

  setGeneration((value) => value + 1);
}

function canApply(readId: number, observedEpoch: number): boolean {
  return isCurrentSessionRead(readId) && sessionStore.getSnapshot().epoch === observedEpoch;
}

function applyLoadedSession(
  result: BrowserSessionLoad,
  observedEpoch: number,
  readId: number,
  logoutCheckRef: { current: boolean },
  setLogoutUncertain: (uncertain: boolean) => void,
  setLogoutError: (message: string | null) => void,
  setLocalSignOut: (signedOut: boolean) => void,
  setPhase: (phase: Phase) => void,
): void {
  if (result.kind === 'anonymous' || result.kind === 'restricted') {
    if (commitBrowserSession(observedEpoch, readId, result.session) === 'stale') {
      return;
    }

    if (result.kind === 'restricted') {
      setLocalSignOut(false);
    }

    finishUnconfirmedSignOut(logoutCheckRef, setLogoutUncertain, setLogoutError, result.kind === 'restricted');
    setPhase(result.kind);
    return;
  }

  finishUnconfirmedSignOut(logoutCheckRef, setLogoutUncertain, setLogoutError, false);
  setPhase('error');
}

function finishUnconfirmedSignOut(
  logoutCheckRef: { current: boolean },
  setLogoutUncertain: (uncertain: boolean) => void,
  setLogoutError: (message: string | null) => void,
  stillSignedIn: boolean,
): void {
  if (!logoutCheckRef.current) {
    return;
  }

  logoutCheckRef.current = false;
  setLogoutUncertain(false);
  setLogoutError(stillSignedIn ? unconfirmedSignOutMessage : null);
}

function beginUnconfirmedSignOut(
  life: GateLife,
  logoutCheckRef: { current: boolean },
  setLogoutUncertain: (uncertain: boolean) => void,
  setLogoutError: SetMessage,
  setPhase: SetPhase,
  setGeneration: SetGeneration,
): void {
  publishSessionRevalidation();
  logoutCheckRef.current = true;
  setLogoutError(null);
  setLogoutUncertain(true);
  claimSessionRefresh(life, setPhase, setGeneration, 'initializing');
}

async function selectOrganization(
  life: GateLife,
  organizationId: string,
  setPending: (pending: boolean) => void,
  setSelectionMessage: SetMessage,
  setPhase: SetPhase,
  setGeneration: SetGeneration,
  phaseRef: PhaseRef,
  setLocalSignOut: (signedOut: boolean) => void,
  setLogoutUncertain: (uncertain: boolean) => void,
  setLogoutError: SetMessage,
  logoutCheckRef: { current: boolean },
): Promise<void> {
  const csrfToken = sessionStore.getSnapshot().csrfToken;

  if (csrfToken === null || !life.alive) {
    setSelectionMessage('The organization request was rejected. Try again when you are ready.');
    return;
  }

  life.mutation = true;
  beginSessionRead();
  setPending(true);
  setSelectionMessage(null);
  setPhase('reconciling');

  try {
    const response = await fetch('/api/session/organization', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        'X-CSRF-Token': csrfToken,
      },
      body: JSON.stringify({ organizationId }),
    });

    if (!life.alive) {
      return;
    }

    if (response.status === 204) {
      publishSessionRevalidation();
      await verifyAfterCookieMutation(
        life,
        phaseRef,
        setPhase,
        setGeneration,
        setSelectionMessage,
        setLocalSignOut,
        setLogoutUncertain,
        setLogoutError,
        logoutCheckRef,
      );
      return;
    }

    const body = await response.text();

    if (!life.alive) {
      return;
    }

    const code = readProblemCode(body);

    if (response.status === 400 && code === 'auth.csrf.invalid') {
      setSelectionMessage('The organization request was rejected. Try again when you are ready.');
    } else if (response.status === 403) {
      setSelectionMessage('That organization is not available.');
    } else if (response.status !== 401) {
      publishSessionRevalidation();
    }

    await verifyAfterCookieMutation(
      life,
      phaseRef,
      setPhase,
      setGeneration,
      setSelectionMessage,
      setLocalSignOut,
      setLogoutUncertain,
      setLogoutError,
      logoutCheckRef,
    );
  } catch (error) {
    if (!life.alive || isAbortError(error)) {
      return;
    }

    publishSessionRevalidation();
    await verifyAfterCookieMutation(
      life,
      phaseRef,
      setPhase,
      setGeneration,
      setSelectionMessage,
      setLocalSignOut,
      setLogoutUncertain,
      setLogoutError,
      logoutCheckRef,
    );
  } finally {
    life.mutation = false;
    setPending(false);

    if (life.alive && life.owed) {
      life.owed = false;
      life.hintOwner = -1;
      setGeneration((value) => value + 1);
    }
  }
}

async function verifyAfterCookieMutation(
  life: GateLife,
  phaseRef: PhaseRef,
  setPhase: SetPhase,
  setGeneration: SetGeneration,
  setSelectionMessage: SetMessage,
  setLocalSignOut: (signedOut: boolean) => void,
  setLogoutUncertain: (uncertain: boolean) => void,
  setLogoutError: SetMessage,
  logoutCheckRef: { current: boolean },
): Promise<void> {
  if (!life.alive) {
    return;
  }

  const readId = beginSessionRead();
  life.owed = false;
  life.hintOwner = readId;
  const observedEpoch = sessionStore.getSnapshot().epoch;

  try {
    const result = await loadBrowserSession(new AbortController().signal);

    if (!life.alive || !canApply(readId, observedEpoch)) {
      return;
    }

    applyLoadedSession(
      result,
      observedEpoch,
      readId,
      logoutCheckRef,
      setLogoutUncertain,
      setLogoutError,
      setLocalSignOut,
      setPhase,
    );
  } catch (error) {
    if (!life.alive || isAbortError(error) || !canApply(readId, observedEpoch)) {
      return;
    }

    finishUnconfirmedSignOut(logoutCheckRef, setLogoutUncertain, setLogoutError, false);
    setPhase('error');
  } finally {
    if (life.alive) {
      settleRead(life, readId, phaseRef, setPhase, setGeneration, setSelectionMessage);
    }
  }
}

async function signOut(
  life: GateLife,
  setPending: (pending: boolean) => void,
  setLogoutError: SetMessage,
  setLocalSignOut: (signedOut: boolean) => void,
  setPhase: SetPhase,
  setGeneration: SetGeneration,
  setLogoutUncertain: (uncertain: boolean) => void,
  logoutCheckRef: { current: boolean },
): Promise<void> {
  const csrfToken = sessionStore.getSnapshot().csrfToken;

  if (csrfToken === null || !life.alive) {
    setLogoutError('Sign-out did not complete. The FinOpsDesk session was kept. Try again when you are ready.');
    return;
  }

  life.mutation = true;
  beginSessionRead();
  setPending(true);
  setLogoutError(null);

  try {
    const response = await fetch('/logout', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        'X-CSRF-Token': csrfToken,
      },
    });

    if (!life.alive) {
      return;
    }

    if (response.status === 204) {
      publishSessionRevalidation();
      sessionStore.rotate(null, null, null);
      setLocalSignOut(true);
      claimSessionRefresh(life, setPhase, setGeneration, 'initializing');
      return;
    }

    const body = await response.text();

    if (!life.alive) {
      return;
    }

    if (readProblemCode(body) === 'auth.csrf.invalid') {
      setLogoutError('The sign-out request was rejected. The FinOpsDesk session was kept. Try again when you are ready.');
      claimSessionRefresh(life, setPhase, setGeneration, null);
      return;
    }

    beginUnconfirmedSignOut(life, logoutCheckRef, setLogoutUncertain, setLogoutError, setPhase, setGeneration);
  } catch (error) {
    if (!life.alive || isAbortError(error)) {
      return;
    }

    beginUnconfirmedSignOut(life, logoutCheckRef, setLogoutUncertain, setLogoutError, setPhase, setGeneration);
  } finally {
    life.mutation = false;
    setPending(false);

    if (life.alive && life.owed) {
      life.owed = false;
      life.hintOwner = -1;
      setGeneration((value) => value + 1);
    }
  }
}
