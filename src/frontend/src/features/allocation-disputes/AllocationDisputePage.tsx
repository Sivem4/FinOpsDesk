import { zodResolver } from '@hookform/resolvers/zod';
import { Alert, Button, Stack, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useSearchParams } from 'react-router';

import { ApiError, NetworkError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { endSession, useSession } from '../../shared/auth/session';
import {
  allocationDisputeDetailQueryKey,
  allocationDisputeIdsMatch,
  allocationDisputeNextEnabled,
  getAllocationDispute,
  listAllocationDisputes,
  readAllocationDisputeAddress,
  type AllocationDisputeList,
  type AllocationDisputeQueueCursorPage,
  type AllocationDisputeRecord,
} from './allocation-dispute-api';
import {
  allocationDisputeFilterSchema,
  type AllocationDisputeFilterValues,
} from './allocation-dispute-filters';
import {
  allocationDisputeQueueQueryKey,
  allocationDisputeWorkSummaryFreshMs,
  allocationDisputeWorkSummaryQueryKey,
  getAllocationDisputeWorkSummary,
  listAllocationDisputeQueue,
  listAllocationDisputeQueueCursor,
} from './allocation-dispute-work-api';
import { AllocationDisputeCorrectionPanel } from './AllocationDisputeCorrectionPanel';
import { AllocationDisputeResolutionPanel } from './AllocationDisputeResolutionPanel';

export function AllocationDisputePage() {
  const session = useSession();
  const [params, setSearchParams] = useSearchParams();
  const address = readAllocationDisputeAddress(params);
  const readable = session.token !== null;
  const selectedId = address.selectedState === 'valid' ? address.selectedValues[0] ?? null : null;
  const cursorMode = address.listValid && address.mode === 'queue-cursor' && address.queue !== null;
  const summary = useQuery({
    queryKey: allocationDisputeWorkSummaryQueryKey(session.epoch),
    enabled: readable && cursorMode,
    staleTime: allocationDisputeWorkSummaryFreshMs,
    queryFn: ({ signal }) => getAllocationDisputeWorkSummary(signal),
  });
  const listQuery = useQuery({
    queryKey: address.queue === null
      ? ['allocation-disputes', 'list', session.epoch, address.page, address.allocationId]
      : allocationDisputeQueueQueryKey(
        session.epoch,
        address.queue,
        address.mode === 'queue-cursor'
          ? { mode: 'cursor', cursor: address.cursor }
          : { mode: 'offset', page: address.page ?? 1 },
      ),
    enabled: readable && address.listValid,
    queryFn: ({ signal }) => {
      if (address.mode === 'queue-cursor' && address.queue !== null) {
        return listAllocationDisputeQueueCursor(address.queue, address.cursor, signal).then(
          (page): AllocationDisputeScreenList => ({ kind: 'cursor', page }),
        );
      }

      if (address.page === null) {
        throw new UnexpectedResponseError('The allocation disputes could not be read.');
      }

      const list = address.queue === null
        ? listAllocationDisputes(address.page, address.allocationId, signal)
        : listAllocationDisputeQueue(address.queue, address.page, signal);
      return list.then((value): AllocationDisputeScreenList => ({ kind: 'offset', list: value }));
    },
  });
  const detailQuery = useQuery({
    queryKey: allocationDisputeDetailQueryKey(session.epoch, selectedId ?? ''),
    enabled: readable && selectedId !== null,
    queryFn: ({ signal }) => {
      if (selectedId === null) {
        throw new UnexpectedResponseError('The allocation dispute could not be read.');
      }

      return getAllocationDispute(selectedId, signal);
    },
  });

  function writeRegister(page: number, allocationId: string | null, selectedValues: readonly string[]) {
    const next = new URLSearchParams();
    next.set('page', String(page));
    if (allocationId !== null) {
      next.set('allocationId', allocationId);
    }

    appendSelected(next, selectedValues);
    setSearchParams(next, { replace: false });
  }

  function writeOffsetQueue(
    page: number,
    queue: 'needs-decision' | 'needs-correction',
    selectedValues: readonly string[],
  ) {
    const next = new URLSearchParams();
    next.set('page', String(page));
    next.set('queue', queue);
    appendSelected(next, selectedValues);
    setSearchParams(next, { replace: false });
  }

  function writeCursorQueue(
    cursor: string | null,
    queue: 'needs-decision' | 'needs-correction',
    selectedValues: readonly string[],
  ) {
    const next = new URLSearchParams();
    next.set('queue', queue);
    if (cursor !== null) {
      next.set('cursor', cursor);
    }

    appendSelected(next, selectedValues);
    setSearchParams(next, { replace: false });
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">Allocation disputes</Typography>
      <Typography color="text.secondary">
        These are stored dispute registrations. A later reversal does not remove one or change what it recorded.
      </Typography>
      {!readable ? <Alert severity="warning">Allocation disputes are not available for this session.</Alert> : null}
      {readable && !address.listValid ? (
        <Alert severity="warning">This allocation dispute address can’t be opened.</Alert>
      ) : null}
      {readable && address.listValid ? (
        <AllocationDisputeFilter
          allocationId={address.allocationId ?? ''}
          onApply={(allocationId) => {
            writeRegister(1, allocationId, address.selectedValues);
          }}
        />
      ) : null}
      {readable && address.listValid && address.queue !== null ? (
        <Stack spacing={1}>
          <Typography>
            This queue is not filtered by allocation. Applying the allocation filter leaves the queue and opens the register.
          </Typography>
          <Button
            variant="outlined"
            sx={{ alignSelf: 'flex-start' }}
            onClick={() => {
              writeRegister(1, address.allocationId, address.selectedValues);
            }}
          >
            Show the full register
          </Button>
        </Stack>
      ) : null}
      {readable && address.selectedState === 'invalid' ? (
        <Alert severity="warning">
          The selected allocation dispute address is not valid. The list can still be read.
        </Alert>
      ) : null}
      {readable && address.queue !== null && address.cursorInAddress ? (
        <BackToStartButton
          onClick={() => {
            const queue = address.queue;
            if (queue !== null) {
              writeCursorQueue(null, queue, address.selectedValues);
            }
          }}
        />
      ) : null}
      {readable && address.listValid ? (
        <AllocationDisputeList
          query={listQuery}
          allocationId={address.allocationId}
          page={address.page ?? 1}
          selectedId={selectedId}
          cursor={address.mode === 'queue-cursor' ? address.cursor : null}
          countLabel={cursorCountLabel(address.queue, summary.data, summary.isError || summary.isLoading)}
          onPage={(page) => {
            if (address.queue === null) {
              writeRegister(page, address.allocationId, address.selectedValues);
              return;
            }

            writeOffsetQueue(page, address.queue, address.selectedValues);
          }}
          onCursor={(cursor) => {
            if (address.queue !== null) {
              writeCursorQueue(cursor, address.queue, address.selectedValues);
            }
          }}
          onSelect={(disputeId) => {
            if (address.mode === 'queue-cursor' && address.queue !== null) {
              writeCursorQueue(address.cursor, address.queue, [disputeId]);
              return;
            }

            if (address.queue !== null) {
              writeOffsetQueue(address.page ?? 1, address.queue, [disputeId]);
              return;
            }

            writeRegister(address.page ?? 1, address.allocationId, [disputeId]);
          }}
        />
      ) : null}
      {readable && selectedId !== null ? (
        <AllocationDisputeDetail query={detailQuery} />
      ) : null}
      {readable ? <AllocationDisputeResolutionPanel epoch={session.epoch} disputeId={selectedId} /> : null}
      {readable ? (
        <AllocationDisputeCorrectionPanel epoch={session.epoch} disputeId={selectedId} />
      ) : null}
    </Stack>
  );
}

function AllocationDisputeFilter({
  allocationId,
  onApply,
}: {
  allocationId: string;
  onApply: (allocationId: string | null) => void;
}) {
  const form = useForm<AllocationDisputeFilterValues>({
    resolver: zodResolver(allocationDisputeFilterSchema),
    defaultValues: { allocationId },
  });
  const { reset } = form;

  useEffect(() => {
    reset({ allocationId });
  }, [allocationId, reset]);

  return (
    <Stack
      spacing={1}
      component="form"
      onSubmit={form.handleSubmit((values) => {
        onApply(values.allocationId === '' ? null : values.allocationId);
      })}
    >
      <Controller
        name="allocationId"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Allocation id"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message}
          />
        )}
      />
      <Button type="submit" variant="contained" sx={{ alignSelf: 'flex-start' }}>Apply filter</Button>
    </Stack>
  );
}

function AllocationDisputeList({
  query,
  allocationId,
  page,
  selectedId,
  cursor,
  countLabel,
  onPage,
  onCursor,
  onSelect,
}: {
  query: ListQuery;
  allocationId: string | null;
  page: number;
  selectedId: string | null;
  cursor: string | null;
  countLabel: string | null;
  onPage: (page: number) => void;
  onCursor: (cursor: string | null) => void;
  onSelect: (disputeId: string) => void;
}) {
  if (query.isLoading) {
    return <Alert severity="info">Loading allocation disputes</Alert>;
  }

  if (query.isError) {
    return (
      <DisputeQueryError
        error={query.error}
        onRetry={() => {
          void query.refetch();
        }}
        retrying={query.isFetching}
        notFound={null}
      />
    );
  }

  if (query.data === undefined) {
    return null;
  }

  const data = query.data;
  if (data.kind === 'cursor') {
    const nextEnabled = data.page.nextCursor !== null;
    const previousEnabled = data.page.previousCursor !== null;
    return (
      <Stack spacing={1}>
        <Typography>Dispute page</Typography>
        <Typography>{countLabel ?? 'Dispute count is unavailable.'}</Typography>
        <Stack direction="row" spacing={1}>
          <Button
            variant="outlined"
            disabled={!previousEnabled || query.isFetching}
            onClick={() => onCursor(data.page.previousCursor)}
          >
            Previous page
          </Button>
          <Button
            variant="outlined"
            disabled={!nextEnabled || query.isFetching}
            onClick={() => onCursor(data.page.nextCursor)}
          >
            Next page
          </Button>
        </Stack>
        {data.page.items.length === 0 ? (
          <Alert severity="info">
            {cursor === null
              ? 'No allocation disputes are stored for this organization.'
              : 'This page has no stored allocation disputes.'}
          </Alert>
        ) : null}
        <DisputeItems items={data.page.items} selectedId={selectedId} onSelect={onSelect} />
      </Stack>
    );
  }

  const offset = data.list;
  const nextEnabled = allocationDisputeNextEnabled(page, offset.items.length, offset.totalCountValue);
  const noRows = offset.items.length === 0 && offset.totalCountValue === 0n;

  return (
    <Stack spacing={1}>
      <Typography>{`Dispute page ${offset.page}`}</Typography>
      <Typography>{`Dispute count ${offset.totalCount}`}</Typography>
      <Stack direction="row" spacing={1}>
        <Button variant="outlined" disabled={page <= 1 || query.isFetching} onClick={() => onPage(page - 1)}>
          Previous page
        </Button>
        <Button variant="outlined" disabled={!nextEnabled || query.isFetching} onClick={() => onPage(page + 1)}>
          Next page
        </Button>
      </Stack>
      {noRows ? (
        <Alert severity="info">
          {allocationId === null
            ? 'No allocation disputes are stored for this organization.'
            : 'No allocation disputes are stored for this allocation.'}
        </Alert>
      ) : null}
      {offset.items.length === 0 && offset.totalCountValue > 0n ? (
        <Alert severity="info">
          {`This page has no stored allocation disputes. Dispute count ${offset.totalCount}.`}
        </Alert>
      ) : null}
      <DisputeItems items={offset.items} selectedId={selectedId} onSelect={onSelect} />
    </Stack>
  );
}

function DisputeItems({
  items,
  selectedId,
  onSelect,
}: {
  items: readonly AllocationDisputeRecord[];
  selectedId: string | null;
  onSelect: (disputeId: string) => void;
}) {
  return (
    <Stack component="ul" aria-label="Stored allocation disputes" spacing={1} sx={{ m: 0, pl: 2 }}>
      {items.map((item, index) => (
        <Stack key={`${item.disputeId}-${index}`} component="li" spacing={0.5}>
          <Typography sx={{ whiteSpace: 'pre-wrap' }}>{item.statement}</Typography>
          <Typography>{`Actor source ${item.actorSource}`}</Typography>
          <Typography sx={{ whiteSpace: 'pre-wrap' }}>{item.actorId}</Typography>
          <Typography>{item.recordedAt}</Typography>
          <Typography>{item.allocationId}</Typography>
          <Typography>{item.disputeId}</Typography>
          <Button
            variant="text"
            aria-pressed={selectedId !== null && allocationDisputeIdsMatch(item.disputeId, selectedId)}
            onClick={() => onSelect(item.disputeId)}
            sx={{ alignSelf: 'flex-start' }}
          >
            {`Show allocation dispute ${item.disputeId}`}
          </Button>
        </Stack>
      ))}
    </Stack>
  );
}

function BackToStartButton({ onClick }: { onClick: () => void }) {
  return (
    <Button variant="outlined" onClick={onClick} sx={{ alignSelf: 'flex-start' }}>
      Back to start
    </Button>
  );
}

function cursorCountLabel(
  queue: 'needs-decision' | 'needs-correction' | null,
  summary: { needsDecision: string; needsCorrection: string } | undefined,
  unavailable: boolean,
): string | null {
  if (queue === null) {
    return null;
  }

  if (unavailable || summary === undefined) {
    return null;
  }

  const count = queue === 'needs-decision' ? summary.needsDecision : summary.needsCorrection;
  return `Dispute count ${count}`;
}

function appendSelected(params: URLSearchParams, selectedValues: readonly string[]) {
  for (const value of selectedValues) {
    params.append('selected', value);
  }
}

function AllocationDisputeDetail({ query }: { query: DetailQuery }) {
  return (
    <Stack spacing={1} component="section" aria-label="Allocation dispute registration">
      <Typography variant="h6" component="h2">Selected registration</Typography>
      {query.isLoading ? <Alert severity="info">Loading the selected allocation dispute</Alert> : null}
      {query.isError ? (
        <DisputeQueryError
          error={query.error}
          onRetry={() => {
            void query.refetch();
          }}
          retrying={query.isFetching}
          notFound="That allocation dispute was not found."
        />
      ) : null}
      {query.isSuccess && query.data !== undefined ? <AllocationDisputeFields record={query.data} /> : null}
    </Stack>
  );
}

function AllocationDisputeFields({ record }: { record: AllocationDisputeRecord }) {
  return (
    <Stack spacing={0.5}>
      <Typography sx={{ whiteSpace: 'pre-wrap' }}>{record.statement}</Typography>
      <Typography>{`Actor source ${record.actorSource}`}</Typography>
      <Typography sx={{ whiteSpace: 'pre-wrap' }}>{record.actorId}</Typography>
      <Typography>{record.recordedAt}</Typography>
      <Typography>{record.allocationId}</Typography>
      <Typography>{record.disputeId}</Typography>
    </Stack>
  );
}

function DisputeQueryError({
  error,
  onRetry,
  retrying,
  notFound,
}: {
  error: unknown;
  onRetry: () => void;
  retrying: boolean;
  notFound: string | null;
}) {
  if (error instanceof StaleSessionError) {
    return null;
  }

  if (error instanceof ApiError && error.status === 401) {
    return (
      <Alert
        severity="warning"
        action={(
          <Button color="inherit" onClick={() => endSession('unauthorized')}>
            Update access
          </Button>
        )}
      >
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    return (
      <Alert severity="warning" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        You don’t have access to allocation disputes.
      </Alert>
    );
  }

  if (notFound !== null && error instanceof ApiError && error.status === 404) {
    return <Alert severity="warning">{notFound}</Alert>;
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
        We couldn’t read this allocation dispute response.
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      Allocation disputes could not be loaded.
    </Alert>
  );
}

function RetryButton({ onRetry, retrying }: { onRetry: () => void; retrying: boolean }) {
  return (
    <Button color="inherit" onClick={onRetry} disabled={retrying}>
      Try again
    </Button>
  );
}

type AllocationDisputeScreenList =
  | { kind: 'offset'; list: AllocationDisputeList }
  | { kind: 'cursor'; page: AllocationDisputeQueueCursorPage };

type ListQuery = ReturnType<typeof useQuery<AllocationDisputeScreenList, Error>>;
type DetailQuery = ReturnType<typeof useQuery<AllocationDisputeRecord, Error>>;
