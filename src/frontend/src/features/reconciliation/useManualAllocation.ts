import { useEffect, useRef, useState } from 'react';

import { sessionStore } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import { invalidateVerifiedAllocation } from './allocation-completion';
import {
  abandonExplicitAmountAttempt,
  beginExplicitAmountAttempt,
  submitExplicitAmountAttempt,
  type BeginExplicitAmountInput,
  type VerifiedExplicitAmount,
} from './allocation-attempt';

interface EpochFailure {
  epoch: number;
  error: unknown;
}

export function useManualAllocation() {
  const session = useSession();
  const mounted = useRef(true);
  const inFlight = useRef(false);
  const [submitting, setSubmitting] = useState(false);
  const [failure, setFailure] = useState<EpochFailure | null>(null);
  const [result, setResult] = useState<VerifiedExplicitAmount | null>(null);

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

  async function run(task: () => Promise<VerifiedExplicitAmount>): Promise<void> {
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
      invalidateVerifiedAllocation(verified);

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

  function allocateNew(input: BeginExplicitAmountInput): Promise<void> {
    return run(() => {
      beginExplicitAmountAttempt(input);
      return submitExplicitAmountAttempt();
    });
  }

  function retryStoredAttempt(): Promise<void> {
    return run(() => submitExplicitAmountAttempt());
  }

  function abandonStoredAttempt(): void {
    const operationEpoch = sessionStore.getSnapshot().epoch;

    try {
      abandonExplicitAmountAttempt();

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
    allocateNew,
    retryStoredAttempt,
    abandonStoredAttempt,
  };
}
