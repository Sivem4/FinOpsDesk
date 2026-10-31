import { useEffect, useRef, useState } from 'react';

import { StaleSessionError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import { getReversalContext } from './reversal-api';
import { invalidateVerifiedReversal } from './reversal-completion';
import {
  abandonReversalAttempt,
  beginReversalAttempt,
  inspectReversalAttempt,
  ReversalAuthenticationError,
  ReversalPreflightError,
  submitReversalAttempt,
  UnresolvedReversalAttemptError,
  type VerifiedReversal,
} from './reversal-attempt';

interface EpochFailure {
  epoch: number;
  error: unknown;
}

export function useReversal() {
  const session = useSession();
  const mounted = useRef(true);
  const inFlight = useRef(false);
  const [submitting, setSubmitting] = useState(false);
  const [failure, setFailure] = useState<EpochFailure | null>(null);
  const [result, setResult] = useState<VerifiedReversal | null>(null);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  if (result !== null && result.epoch !== session.epoch) {
    setResult(null);
  }

  if (failure !== null && failure.epoch !== session.epoch) {
    setFailure(null);
  }

  const visibleResult = result !== null && result.epoch === session.epoch ? result : null;
  const visibleError = failure !== null && failure.epoch === session.epoch ? failure.error : null;

  function dismiss(): void {
    setFailure(null);
    setResult(null);
  }

  async function run(task: () => Promise<VerifiedReversal>): Promise<void> {
    if (inFlight.current) {
      return;
    }

    const operationEpoch = sessionStore.getSnapshot().epoch;
    inFlight.current = true;

    if (mounted.current) {
      setSubmitting(true);
      setFailure(null);
      setResult(null);
    }

    try {
      const verified = await task();
      invalidateVerifiedReversal(verified);

      if (mounted.current && sessionStore.getSnapshot().epoch === verified.epoch) {
        setResult(verified);
        setFailure(null);
      }
    } catch (error) {
      if (mounted.current && sessionStore.getSnapshot().epoch === operationEpoch) {
        setFailure({ epoch: operationEpoch, error });
      }
    } finally {
      inFlight.current = false;

      if (mounted.current) {
        setSubmitting(false);
      }
    }
  }

  function reverseNew(allocationId: string, reason: string): Promise<void> {
    return run(async () => {
      const current = sessionStore.getSnapshot();

      if (current.token === null) {
        throw new ReversalAuthenticationError();
      }

      if (inspectReversalAttempt(current).status !== 'none') {
        throw new UnresolvedReversalAttemptError();
      }

      const context = await getReversalContext(allocationId, current.signal);

      if (sessionStore.getSnapshot().epoch !== current.epoch) {
        throw new StaleSessionError();
      }

      if (context.status !== 'confirmed') {
        throw new ReversalPreflightError();
      }

      beginReversalAttempt({
        allocationId: context.allocationId,
        invoiceId: context.invoiceId,
        bankTransactionId: context.bankTransactionId,
        amount: context.amount,
        currency: context.currency,
        reason,
      });
      return submitReversalAttempt();
    });
  }

  function retryStoredAttempt(): Promise<void> {
    return run(() => submitReversalAttempt());
  }

  function abandonStoredAttempt(idempotencyKey: string): void {
    const operationEpoch = sessionStore.getSnapshot().epoch;

    try {
      abandonReversalAttempt(idempotencyKey);

      if (mounted.current) {
        setFailure(null);
        setResult(null);
      }
    } catch (error) {
      if (mounted.current && sessionStore.getSnapshot().epoch === operationEpoch) {
        setFailure({ epoch: operationEpoch, error });
      }
    }
  }

  return {
    submitting,
    error: visibleError,
    result: visibleResult,
    dismiss,
    reverseNew,
    retryStoredAttempt,
    abandonStoredAttempt,
  };
}
