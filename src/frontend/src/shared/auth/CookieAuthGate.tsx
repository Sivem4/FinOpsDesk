import { Alert, Button, Container, Stack, Typography } from '@mui/material';
import { useEffect, useRef, useState } from 'react';

import { isAbortError } from '../api/errors';
import { sessionStore } from '../api/session-store';
import { commitBrowserSession, loadBrowserSession, readProblemCode } from './browser-session';
import { useSession } from './session';

type Phase = 'initializing' | 'anonymous' | 'restricted' | 'error';

const unconfirmedSignOutMessage =
  'Sign-out could not be confirmed. You are still signed in to FinOpsDesk in this browser. Try again when you are ready.';

export function CookieAuthGate() {
  const session = useSession();
  const [phase, setPhase] = useState<Phase>('initializing');
  const [generation, setGeneration] = useState(0);
  const [logoutError, setLogoutError] = useState<string | null>(null);
  const [localSignOut, setLocalSignOut] = useState(false);
  const [pending, setPending] = useState(false);
  const [logoutUncertain, setLogoutUncertain] = useState(false);
  const logoutCheckRef = useRef(false);

  useEffect(() => {
    const controller = new AbortController();
    let cancelled = false;
    const observedEpoch = sessionStore.getSnapshot().epoch;

    void loadBrowserSession(controller.signal)
      .then((result) => {
        if (cancelled || sessionStore.getSnapshot().epoch !== observedEpoch) {
          return;
        }

        if (result.kind === 'anonymous' || result.kind === 'restricted') {
          if (commitBrowserSession(observedEpoch, result.session) === 'stale') {
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
      })
      .catch((error: unknown) => {
        if (cancelled || isAbortError(error) || sessionStore.getSnapshot().epoch !== observedEpoch) {
          return;
        }

        finishUnconfirmedSignOut(logoutCheckRef, setLogoutUncertain, setLogoutError, false);
        setPhase('error');
      });

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [generation]);

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
        <Typography>
          Sign out ends the FinOpsDesk session in this browser. The identity provider may keep its own session.
        </Typography>
        {logoutError === null ? null : <Alert severity="warning">{logoutError}</Alert>}
        <Button
          type="button"
          variant="outlined"
          disabled={pending}
          onClick={() =>
            void signOut(
              setPending,
              setLogoutError,
              setLocalSignOut,
              setPhase,
              setGeneration,
              setLogoutUncertain,
              logoutCheckRef,
            )
          }
        >
          Sign out
        </Button>
      </Stack>
    </Container>
  );
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
  logoutCheckRef: { current: boolean },
  setLogoutUncertain: (uncertain: boolean) => void,
  setLogoutError: (message: string | null) => void,
  setPhase: (phase: Phase) => void,
  setGeneration: (update: (value: number) => number) => void,
): void {
  logoutCheckRef.current = true;
  setLogoutError(null);
  setLogoutUncertain(true);
  setPhase('initializing');
  setGeneration((value) => value + 1);
}

async function signOut(
  setPending: (pending: boolean) => void,
  setLogoutError: (message: string | null) => void,
  setLocalSignOut: (signedOut: boolean) => void,
  setPhase: (phase: Phase) => void,
  setGeneration: (update: (value: number) => number) => void,
  setLogoutUncertain: (uncertain: boolean) => void,
  logoutCheckRef: { current: boolean },
): Promise<void> {
  const csrfToken = sessionStore.getSnapshot().csrfToken;

  if (csrfToken === null) {
    setLogoutError('Sign-out did not complete. The FinOpsDesk session was kept. Try again when you are ready.');
    return;
  }

  setPending(true);
  setLogoutError(null);
  const observedEpoch = sessionStore.getSnapshot().epoch;

  try {
    const response = await fetch('/logout', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        'X-CSRF-Token': csrfToken,
      },
    });

    if (sessionStore.getSnapshot().epoch !== observedEpoch) {
      return;
    }

    if (response.status === 204) {
      sessionStore.rotate(null, null, null);
      setLocalSignOut(true);
      setPhase('initializing');
      setGeneration((value) => value + 1);
      return;
    }

    const body = await response.text();

    if (readProblemCode(body) === 'auth.csrf.invalid') {
      setLogoutError('The sign-out request was rejected. The FinOpsDesk session was kept. Try again when you are ready.');
      setGeneration((value) => value + 1);
      return;
    }

    beginUnconfirmedSignOut(logoutCheckRef, setLogoutUncertain, setLogoutError, setPhase, setGeneration);
  } catch (error) {
    if (isAbortError(error) || sessionStore.getSnapshot().epoch !== observedEpoch) {
      return;
    }

    beginUnconfirmedSignOut(logoutCheckRef, setLogoutUncertain, setLogoutError, setPhase, setGeneration);
  } finally {
    setPending(false);
  }
}
