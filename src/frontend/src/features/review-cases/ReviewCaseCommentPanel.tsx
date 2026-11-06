import { zodResolver } from '@hookform/resolvers/zod';
import { Alert, AlertTitle, Button, Stack, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';

import { ApiError, NetworkError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { browserAuthMode } from '../../shared/auth/auth-mode';
import { endSession, useSession } from '../../shared/auth/session';
import {
  listReviewCaseComments,
  reviewCaseCommentLastPage,
  reviewCaseCommentNextEnabled,
  reviewCaseCommentPageSize,
  type ReviewCaseCommentRecord,
} from './review-case-comment-api';
import {
  abandonReviewCaseCommentAttempt,
  abandonUnreadableReviewCaseCommentAttempt,
  beginReviewCaseCommentAttempt,
  ReviewCaseCommentActorRequiredError,
  ReviewCaseCommentAttemptBlockedError,
  ReviewCaseCommentAttemptChangedError,
  ReviewCaseCommentAuthenticationError,
  ReviewCaseCommentPayloadConflictError,
  ReviewCaseCommentRejectedError,
  ReviewCaseCommentStorageCleanupError,
  ReviewCaseCommentStorageError,
  ReviewCaseCommentSubmissionInProgressError,
  ReviewCaseCommentTenantMismatchError,
  ReviewCaseCommentUncertaintyRemainsError,
  submitReviewCaseCommentAttempt,
  UnknownReviewCaseCommentOutcomeError,
  UnreadableReviewCaseCommentError,
  UnresolvedReviewCaseCommentError,
  useReviewCaseCommentAttempt,
  type ReviewCaseCommentAttempt,
  type VerifiedReviewCaseComment,
} from './review-case-comment-attempt';
import {
  normalizeReviewCaseCommentText,
  reviewCaseCommentSchema,
  type ReviewCaseCommentFormValues,
} from './review-case-comment-request';
import { developmentActorId, developmentOrganizationId } from './review-case-open-request';

const emptyForm: ReviewCaseCommentFormValues = { text: '' };

export function ReviewCaseCommentRecovery() {
  const view = useReviewCaseCommentAttempt();
  const session = useSession();
  const queryClient = useQueryClient();
  const resultGeneration = useRef(0);
  const viewMounted = useRef(false);
  const [error, setError] = useState<unknown>(null);
  const [verified, setVerified] = useState<VerifiedReviewCaseComment | null>(null);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [viewEpoch, setViewEpoch] = useState(session.epoch);
  if (viewEpoch !== session.epoch) {
    setViewEpoch(session.epoch);
    setVerified(null);
    setError(null);
    setRefreshFailed(false);
    setRefreshing(false);
  }
  useEffect(() => {
    viewMounted.current = true;
    return () => {
      viewMounted.current = false;
    };
  }, []);

  if (view.status === 'blocked-authentication' || (view.status === 'none' && verified === null && error === null)) {
    return (
      <Typography color="text.secondary">
        Two people using this organization in the same tab can see one stored comment. The record follows the organization id and does not identify the operator.
      </Typography>
    );
  }

  function ownsResult(epoch: number, generation: number): boolean {
    return viewMounted.current
      && viewEpoch === epoch
      && sessionStore.getSnapshot().epoch === epoch
      && resultGeneration.current === generation;
  }

  function publishError(next: unknown): void {
    if (!viewMounted.current || sessionStore.getSnapshot().epoch !== viewEpoch) {
      return;
    }

    setError(next);
  }

  function acceptResult(result: VerifiedReviewCaseComment): number | null {
    if (!ownsResult(result.epoch, resultGeneration.current)) {
      return null;
    }

    resultGeneration.current += 1;
    setVerified(result);
    setError(result.cleanup === 'failed' ? new ReviewCaseCommentStorageCleanupError() : null);
    setRefreshFailed(false);
    return resultGeneration.current;
  }

  async function applyVerified(result: VerifiedReviewCaseComment): Promise<void> {
    if (!viewMounted.current) {
      return;
    }

    const generation = acceptResult(result);
    if (generation === null) {
      return;
    }

    try {
      await refreshReviewCaseComments(queryClient, result.epoch, result.caseId);
      if (!ownsResult(result.epoch, generation)) {
        return;
      }

      setRefreshFailed(false);
    } catch {
      if (!ownsResult(result.epoch, generation)) {
        return;
      }

      setRefreshFailed(true);
    }
  }

  function retryReading(result: VerifiedReviewCaseComment): void {
    const generation = resultGeneration.current;
    const epoch = result.epoch;
    setRefreshing(true);
    void refreshReviewCaseComments(queryClient, epoch, result.caseId)
      .then(() => {
        if (ownsResult(epoch, generation)) {
          setRefreshFailed(false);
        }
      })
      .catch(() => {
        if (ownsResult(epoch, generation)) {
          setRefreshFailed(true);
        }
      })
      .finally(() => {
        if (viewMounted.current && sessionStore.getSnapshot().epoch === viewEpoch) {
          setRefreshing(false);
        }
      });
  }

  const canComment = developmentOrganizationId(session.token) !== null && developmentActorId(session.token) !== null;

  return (
    <Stack spacing={1} component="section" aria-label="Review case comment recovery">
      <Typography color="text.secondary">
        Two people using this organization in the same tab can see one stored comment. The record follows the organization id and does not identify the operator.
      </Typography>
      {view.status === 'invalid-storage' ? <UnreadableStorage onClear={() => publishError(null)} onError={publishError} /> : null}
      {view.status === 'blocked-tenant' ? (
        <Alert severity="warning">A stored comment belongs to another organization in this tab. It was not replaced and it was not sent.</Alert>
      ) : null}
      {view.status === 'pending' ? (
        <StoredComment
          attempt={view.attempt}
          canSend={canComment}
          onVerified={(result) => void applyVerified(result)}
          onError={publishError}
        />
      ) : null}
      {verified !== null ? <VerifiedComment result={verified} /> : null}
      {refreshFailed && verified !== null ? (
        <Alert
          severity="warning"
          action={(
            <Button color="inherit" disabled={refreshing} onClick={() => retryReading(verified)}>
              Retry reading comments
            </Button>
          )}
        >
          The comment is confirmed. The comment list could not be refreshed.
        </Alert>
      ) : null}
      {error !== null ? <Alert severity="warning">{failureMessage(error)}</Alert> : null}
    </Stack>
  );
}

export function ReviewCaseCommentsHost({ reviewCaseId }: { reviewCaseId: string }) {
  const [open, setOpen] = useState(false);
  const panelId = `review-case-comments-${reviewCaseId}`;

  return (
    <Stack spacing={1} sx={{ mt: 2 }}>
      <Button
        type="button"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={() => setOpen((current) => !current)}
      >
        {open ? 'Hide comments' : 'View comments'}
      </Button>
      {open ? <ReviewCaseCommentsPanel id={panelId} reviewCaseId={reviewCaseId} /> : null}
    </Stack>
  );
}

export function ReviewCaseCommentsPanel({
  id,
  reviewCaseId,
  initialPage = 1,
}: {
  id?: string;
  reviewCaseId: string;
  initialPage?: number;
}) {
  const session = useSession();
  const cookie = browserAuthMode() === 'cookie';
  const canRead = cookie
    ? session.authorization === 'verified' && session.capabilities.includes('audit.read')
    : session.token !== null;
  const [page, setPage] = useState(() => clampCommentPage(initialPage));
  const query = useQuery({
    queryKey: cookie
      ? ['review-cases', 'comments', session.epoch, session.readiness, reviewCaseId, page, reviewCaseCommentPageSize]
      : ['review-cases', 'comments', session.epoch, reviewCaseId, page, reviewCaseCommentPageSize],
    enabled: canRead,
    queryFn: ({ signal }) => listReviewCaseComments(reviewCaseId, page, signal),
  });
  const comments = query.isSuccess ? query.data : null;
  const nextEnabled = comments !== null && reviewCaseCommentNextEnabled(
    comments.page,
    comments.items.length,
    comments.totalCountValue,
  );

  return (
    <Stack id={id} spacing={1} component="section" aria-label="Review case comments">
      <Typography color="text.secondary">
        Comments are separate from the stored decision history and from the balances above.
      </Typography>
      {query.isPending ? <Alert severity="info" role="status">Loading comments</Alert> : null}
      {query.isError ? (
        <ReviewCaseCommentReadError error={query.error} onRetry={() => void query.refetch()} retrying={query.isFetching} />
      ) : null}
      {comments !== null && comments.items.length === 0 ? (
        <Alert severity="info">No comments were found for this case.</Alert>
      ) : null}
      {comments !== null && comments.items.length > 0 ? (
        <Stack component="ol" aria-label="Review case comment entries" spacing={1} sx={{ m: 0, pl: 0, listStyle: 'none' }}>
          {comments.items.map((comment) => <CommentEntry key={comment.id} comment={comment} />)}
        </Stack>
      ) : null}
      {comments !== null ? (
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <Typography>{`Comment count ${comments.totalCount}`}</Typography>
          <Typography>{`Comment page ${comments.page}`}</Typography>
          <Button type="button" disabled={page <= 1 || query.isFetching} onClick={() => setPage((current) => Math.max(1, current - 1))}>
            Previous comments
          </Button>
          <Button
            type="button"
            disabled={!nextEnabled || query.isFetching}
            onClick={() => setPage((current) => {
              if (!reviewCaseCommentNextEnabled(current, comments.items.length, comments.totalCountValue)) {
                return current;
              }

              return Math.min(current + 1, reviewCaseCommentLastPage);
            })}
          >
            Next comments
          </Button>
        </Stack>
      ) : null}
    </Stack>
  );
}

export function ReviewCaseCommentComposer({ reviewCaseId }: { reviewCaseId: string }) {
  const view = useReviewCaseCommentAttempt();
  const session = useSession();
  const queryClient = useQueryClient();
  const resultGeneration = useRef(0);
  const viewMounted = useRef(false);
  const [error, setError] = useState<unknown>(null);
  const [verified, setVerified] = useState<VerifiedReviewCaseComment | null>(null);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  useEffect(() => {
    viewMounted.current = true;
    return () => {
      viewMounted.current = false;
    };
  }, []);
  const canComment = developmentOrganizationId(session.token) !== null && developmentActorId(session.token) !== null;

  function ownsResult(epoch: number, generation: number): boolean {
    return viewMounted.current && sessionStore.getSnapshot().epoch === epoch && resultGeneration.current === generation;
  }

  function acceptResult(result: VerifiedReviewCaseComment): number | null {
    if (!viewMounted.current || sessionStore.getSnapshot().epoch !== result.epoch) {
      return null;
    }

    resultGeneration.current += 1;
    setVerified(result);
    setError(result.cleanup === 'failed' ? new ReviewCaseCommentStorageCleanupError() : null);
    setRefreshFailed(false);
    return resultGeneration.current;
  }

  async function applyVerified(result: VerifiedReviewCaseComment): Promise<void> {
    if (!viewMounted.current) {
      return;
    }

    const generation = acceptResult(result);
    if (generation === null) {
      return;
    }

    try {
      await refreshReviewCaseComments(queryClient, result.epoch, result.caseId);
      if (!ownsResult(result.epoch, generation)) {
        return;
      }

      setRefreshFailed(false);
    } catch {
      if (!ownsResult(result.epoch, generation)) {
        return;
      }

      setRefreshFailed(true);
    }
  }

  function retryReading(result: VerifiedReviewCaseComment): void {
    const generation = resultGeneration.current;
    const epoch = result.epoch;
    setRefreshing(true);
    void refreshReviewCaseComments(queryClient, epoch, result.caseId)
      .then(() => {
        if (ownsResult(epoch, generation)) {
          setRefreshFailed(false);
        }
      })
      .catch(() => {
        if (ownsResult(epoch, generation)) {
          setRefreshFailed(true);
        }
      })
      .finally(() => {
        if (viewMounted.current) {
          setRefreshing(false);
        }
      });
  }

  if (!canComment) {
    return null;
  }

  return (
    <Stack spacing={1} sx={{ mt: 2 }}>
      {view.status === 'none' ? (
        <CommentForm
          reviewCaseId={reviewCaseId}
          onVerified={(result) => void applyVerified(result)}
          onError={setError}
        />
      ) : null}
      {verified !== null ? <VerifiedComment result={verified} /> : null}
      {refreshFailed && verified !== null ? (
        <Alert
          severity="warning"
          action={(
            <Button color="inherit" disabled={refreshing} onClick={() => retryReading(verified)}>
              Retry reading comments
            </Button>
          )}
        >
          The comment is confirmed. The comment list could not be refreshed.
        </Alert>
      ) : null}
      {error !== null ? <Alert severity="warning">{failureMessage(error)}</Alert> : null}
    </Stack>
  );
}

function CommentForm({
  reviewCaseId,
  onVerified,
  onError,
}: {
  reviewCaseId: string;
  onVerified: (result: VerifiedReviewCaseComment) => void;
  onError: (error: unknown) => void;
}) {
  const [sending, setSending] = useState(false);
  const form = useForm<ReviewCaseCommentFormValues>({
    resolver: zodResolver(reviewCaseCommentSchema),
    defaultValues: emptyForm,
  });

  return (
    <Stack
      spacing={1}
      component="form"
      aria-label="Add a review case comment"
      onSubmit={form.handleSubmit(async (values) => {
        const normalized = normalizeReviewCaseCommentText(values.text);
        if (!normalized.ok) {
          return;
        }

        setSending(true);
        onError(null);
        try {
          beginReviewCaseCommentAttempt(reviewCaseId, normalized.text);
          onVerified(await submitReviewCaseCommentAttempt());
        } catch (error) {
          onError(error);
        } finally {
          setSending(false);
        }
      })}
    >
      <Controller
        name="text"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Comment"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message ?? 'Leading and trailing whitespace is removed. A byte-order mark is kept. At most 500 characters after that.'}
            multiline
          />
        )}
      />
      <Button type="submit" variant="contained" disabled={sending}>Add comment</Button>
    </Stack>
  );
}

function StoredComment({
  attempt,
  canSend,
  onVerified,
  onError,
}: {
  attempt: ReviewCaseCommentAttempt;
  canSend: boolean;
  onVerified: (result: VerifiedReviewCaseComment) => void;
  onError: (error: unknown) => void;
}) {
  const [sending, setSending] = useState(false);
  const [confirmRemoval, setConfirmRemoval] = useState(false);
  const unresolved = attempt.lifecycle !== 'rejected';

  return (
    <Stack spacing={1} aria-label="Stored review-case comment">
      <Typography>{`Case ${attempt.caseId}`}</Typography>
      <Typography sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{attempt.normalizedText}</Typography>
      <Typography>
        {unresolved
          ? 'This comment has no verified result. Sending it again uses the same request.'
          : 'The server refused this comment before saving it.'}
      </Typography>
      {unresolved && canSend ? (
        <Button
          variant="contained"
          disabled={sending}
          onClick={() => {
            setSending(true);
            onError(null);
            void submitReviewCaseCommentAttempt()
              .then(onVerified)
              .catch(onError)
              .finally(() => setSending(false));
          }}
        >
          Send the same comment again
        </Button>
      ) : null}
      <Button type="button" onClick={() => setConfirmRemoval(true)}>Remove local tracking</Button>
      {confirmRemoval ? (
        <Stack spacing={1}>
          <Typography>
            Removing local tracking does not undo a server write. It can delete the only local copy of this idempotency key.
          </Typography>
          <Button
            color="warning"
            onClick={() => {
              onError(null);
              try {
                abandonReviewCaseCommentAttempt(attempt.idempotencyKey, attempt.requestBody);
                setConfirmRemoval(false);
              } catch (error) {
                onError(error);
              }
            }}
          >
            Confirm removal of local tracking
          </Button>
        </Stack>
      ) : null}
    </Stack>
  );
}

function UnreadableStorage({ onClear, onError }: { onClear: () => void; onError: (error: unknown) => void }) {
  const [confirmRemoval, setConfirmRemoval] = useState(false);
  return (
    <Stack spacing={1}>
      <Alert severity="warning">The stored comment could not be read. It was not replaced and it was not sent.</Alert>
      <Button type="button" onClick={() => setConfirmRemoval(true)}>Remove local tracking</Button>
      {confirmRemoval ? (
        <Stack spacing={1}>
          <Typography>
            Removing local tracking does not undo a server write. It can delete the only local copy of this idempotency key.
          </Typography>
          <Button
            color="warning"
            onClick={() => {
              try {
                abandonUnreadableReviewCaseCommentAttempt();
                onClear();
              } catch (error) {
                onError(error);
              }
            }}
          >
            Confirm removal of local tracking
          </Button>
        </Stack>
      ) : null}
    </Stack>
  );
}

function VerifiedComment({ result }: { result: VerifiedReviewCaseComment }) {
  return (
    <Alert severity="success">
      <AlertTitle>{result.created ? 'Comment saved.' : 'This comment was already stored.'}</AlertTitle>
      <Typography>{`Case ${result.caseId}`}</Typography>
      <Typography>{`Comment ${result.id}`}</Typography>
      <Typography sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{result.text}</Typography>
      <Typography sx={{ fontFamily: 'monospace', whiteSpace: 'pre-wrap' }}>{`Stored actor ${result.actorId}`}</Typography>
      <Typography>{`Recorded ${result.recordedAt}`}</Typography>
    </Alert>
  );
}

function CommentEntry({ comment }: { comment: ReviewCaseCommentRecord }) {
  return (
    <Stack component="li" spacing={0.5}>
      <Typography>{`Comment ${comment.id}`}</Typography>
      <Typography sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{comment.text}</Typography>
      <Typography sx={{ fontFamily: 'monospace', whiteSpace: 'pre-wrap' }}>{`Actor ${comment.actorId}`}</Typography>
      <Typography>{`Recorded ${comment.recordedAt}`}</Typography>
    </Stack>
  );
}

function ReviewCaseCommentReadError({
  error,
  onRetry,
  retrying,
}: {
  error: unknown;
  onRetry: () => void;
  retrying: boolean;
}) {
  if (error instanceof StaleSessionError) {
    return null;
  }

  if (error instanceof ApiError && error.status === 401) {
    if (browserAuthMode() === 'cookie') {
      return <Alert severity="warning">The browser session needs to be checked again. Comments stay hidden until that check succeeds.</Alert>;
    }

    return (
      <Alert severity="warning" action={<Button color="inherit" onClick={() => endSession('unauthorized')}>Update access</Button>}>
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    if (browserAuthMode() === 'cookie') {
      return (
        <Alert severity="warning" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
          Comments are not available for this organization.
        </Alert>
      );
    }

    return <Alert severity="warning">You do not have access to these comments.</Alert>;
  }

  if (error instanceof ApiError && error.status === 404) {
    return <Alert severity="warning">That review case was not found.</Alert>;
  }

  if (error instanceof NetworkError) {
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        We couldn’t connect. Check your connection and try again.
      </Alert>
    );
  }

  if (error instanceof UnexpectedResponseError) {
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        We couldn’t read the comments.
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      Comments could not be loaded.
    </Alert>
  );
}

function RetryButton({ onRetry, retrying }: { onRetry: () => void; retrying: boolean }) {
  return (
    <Button color="inherit" onClick={onRetry} disabled={retrying}>Try again</Button>
  );
}

function failureMessage(error: unknown): string {
  if (error instanceof ReviewCaseCommentStorageError || error instanceof ReviewCaseCommentAttemptBlockedError || error instanceof UnresolvedReviewCaseCommentError) {
    return 'The comment was not stored, so it was not sent. An existing stored comment was not replaced.';
  }

  if (error instanceof ReviewCaseCommentStorageCleanupError) {
    return 'The server confirmed this comment. This browser could not remove the local recovery record. The original key was not replaced, and the comment was not rolled back.';
  }

  if (error instanceof ReviewCaseCommentAttemptChangedError) {
    return 'The stored comment changed before the response could be applied. The changed record was not removed.';
  }

  if (error instanceof ReviewCaseCommentAuthenticationError || error instanceof ReviewCaseCommentActorRequiredError) {
    return 'This comment needs a current development token and an operator identity of 1 to 256 characters. The request was not sent.';
  }

  if (error instanceof ReviewCaseCommentTenantMismatchError) {
    return 'The stored comment belongs to another organization. It was not sent.';
  }

  if (error instanceof ReviewCaseCommentSubmissionInProgressError) {
    return 'This comment is still being sent. Wait until that request finishes before removing local tracking or starting another one.';
  }

  if (error instanceof ReviewCaseCommentPayloadConflictError) {
    return 'This idempotency key was already used for a different comment. The stored request was not confirmed and was not replaced.';
  }

  if (error instanceof ReviewCaseCommentRejectedError) {
    return 'The server refused this comment before saving it. Remove local tracking before starting another one.';
  }

  if (error instanceof ReviewCaseCommentUncertaintyRemainsError) {
    return 'This retry was refused. The earlier comment stays unresolved, its key was not replaced, and it was not rolled back.';
  }

  if (error instanceof UnknownReviewCaseCommentOutcomeError) {
    return 'The comment was not verified. A lost response does not show that the server rolled it back. Send the same comment again.';
  }

  if (error instanceof UnreadableReviewCaseCommentError) {
    return 'The server response could not be verified. The stored request was kept.';
  }

  if (error instanceof StaleSessionError) {
    return 'The session changed after the request started. The response was not applied here, and the earlier attempt was not rolled back.';
  }

  if (error instanceof ApiError) {
    return error.title;
  }

  return 'The comment was not verified. The stored request was kept.';
}

async function refreshReviewCaseComments(queryClient: QueryClient, epoch: number, reviewCaseId: string): Promise<void> {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    return;
  }

  await queryClient.invalidateQueries({
    predicate: (query) => {
      const key = query.queryKey;
      return key[0] === 'review-cases'
        && key[1] === 'comments'
        && key.includes(epoch)
        && key.includes(reviewCaseId);
    },
  }, { throwOnError: true });
}

function clampCommentPage(page: number): number {
  if (!Number.isSafeInteger(page) || page < 1) {
    return 1;
  }

  return Math.min(page, reviewCaseCommentLastPage);
}
