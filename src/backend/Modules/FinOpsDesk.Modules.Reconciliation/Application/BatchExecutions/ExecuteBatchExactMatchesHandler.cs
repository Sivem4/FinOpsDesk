using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public sealed class ExecuteBatchExactMatchesHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IExactMatchCandidateQueries _candidateQueries;
    private readonly IAllocationIdempotencyLookup _idempotencyLookup;
    private readonly IBatchExecutionOwner _owner;
    private readonly TimeProvider _timeProvider;

    public ExecuteBatchExactMatchesHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IServiceScopeFactory scopeFactory,
        IExactMatchCandidateQueries candidateQueries,
        IAllocationIdempotencyLookup idempotencyLookup,
        IBatchExecutionOwner owner,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(candidateQueries);
        ArgumentNullException.ThrowIfNull(idempotencyLookup);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _scopeFactory = scopeFactory;
        _candidateQueries = candidateQueries;
        _idempotencyLookup = idempotencyLookup;
        _owner = owner;
        _timeProvider = timeProvider;
    }

    public async Task<Result<ExecuteBatchExactMatchesResponse>> Handle(
        ExecuteBatchExactMatchesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        if (command.IdempotencyKey == Guid.Empty || command.Pairs is null)
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchInvalidRequest);
        }

        BatchExactMatchIdentity identity;

        try
        {
            identity = BatchExactMatchIdentity.Create(command.Pairs);
        }
        catch (ArgumentException)
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchInvalidRequest);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        BatchExecutionSnapshot? existing = await FindAsync(
            organizationId,
            command.IdempotencyKey,
            cancellationToken);

        if (existing is null)
        {
            Result<ExecuteBatchExactMatchesResponse>? gate = await GateNewRequestAsync(
                organizationId,
                cancellationToken);

            if (gate is not null)
            {
                return gate;
            }

            var execution = BatchExecution.Start(
                BatchExecutionId.New(),
                organizationId,
                command.IdempotencyKey,
                identity.Fingerprint,
                _timeProvider.GetUtcNow());
            bool inserted = await InsertAsync(execution, cancellationToken);

            if (!inserted)
            {
                existing = await FindAsync(organizationId, command.IdempotencyKey, cancellationToken);

                if (existing is null)
                {
                    return Result<ExecuteBatchExactMatchesResponse>.Failure(
                        ReconciliationApplicationErrors.BatchInfrastructureFailure);
                }
            }
            else
            {
                existing = await FindAsync(organizationId, command.IdempotencyKey, cancellationToken);
            }
        }

        if (existing is null)
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchInfrastructureFailure);
        }

        if (!existing.Fingerprint.Equals(identity.Fingerprint))
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchIdempotencyPayloadConflict);
        }

        if (existing.Status == BatchExecutionStatus.Completed)
        {
            return Stored(existing);
        }

        return await ExecuteRunningAsync(
            organizationId,
            command.IdempotencyKey,
            identity,
            cancellationToken);
    }

    private async Task<Result<ExecuteBatchExactMatchesResponse>> ExecuteRunningAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        BatchExactMatchIdentity identity,
        CancellationToken cancellationToken)
    {
        Result<IBatchExecutionLease> acquired = await _owner.TryAcquireAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);

        if (acquired.IsFailure)
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(acquired.Error);
        }

        IBatchExecutionLease lease = acquired.Value;
        bool committed = false;

        try
        {
            BatchExecutionSnapshot snapshot = lease.Snapshot;

            if (snapshot.OrganizationId != organizationId
                || snapshot.IdempotencyKey != idempotencyKey
                || !snapshot.Fingerprint.Equals(identity.Fingerprint))
            {
                return Result<ExecuteBatchExactMatchesResponse>.Failure(
                    ReconciliationApplicationErrors.BatchIdempotencyPayloadConflict);
            }

            if (snapshot.Status == BatchExecutionStatus.Completed)
            {
                return Stored(snapshot);
            }

            Result<BatchExecutionResult> work = await BuildResultAsync(
                organizationId,
                idempotencyKey,
                identity,
                snapshot,
                cancellationToken);

            if (work.IsFailure)
            {
                return Result<ExecuteBatchExactMatchesResponse>.Failure(work.Error);
            }

            DateTimeOffset completedAt = _timeProvider.GetUtcNow();
            bool completed = await lease.TryCompleteAsync(work.Value, completedAt, cancellationToken);

            if (!completed)
            {
                return Result<ExecuteBatchExactMatchesResponse>.Failure(
                    ReconciliationApplicationErrors.BatchInfrastructureFailure);
            }

            await lease.CommitAsync(cancellationToken);
            committed = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchInfrastructureFailure);
        }
        finally
        {
            await lease.DisposeAsync();
        }

        if (!committed)
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchInfrastructureFailure);
        }

        BatchExecutionSnapshot? stored = await FindAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        return stored is null
            ? Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchInfrastructureFailure)
            : Stored(stored);
    }

    private async Task<Result<BatchExecutionResult>> BuildResultAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        BatchExactMatchIdentity identity,
        BatchExecutionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        BatchExecutionItem[] items = new BatchExecutionItem[identity.Pairs.Count];
        List<int> pending = new(identity.Pairs.Count);

        for (int index = 0; index < identity.Pairs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result<BatchExecutionItem>? recovered = await TryRecoverAsync(
                organizationId,
                idempotencyKey,
                identity.Pairs[index],
                cancellationToken);

            if (recovered is null)
            {
                pending.Add(index);
                continue;
            }

            if (recovered.IsFailure)
            {
                return Result<BatchExecutionResult>.Failure(recovered.Error);
            }

            items[index] = recovered.Value;
        }

        if (pending.Count == 0)
        {
            return Result<BatchExecutionResult>.Success(BatchExecutionResult.FromItems(items));
        }

        DateTimeOffset deadline = snapshot.CreatedAt + BatchExecutionLimits.MaxResumeAge;

        if (_timeProvider.GetUtcNow() >= deadline)
        {
            Assign(items, identity, pending, 0, BatchExecutionItemOutcome.Interrupted);
            return Result<BatchExecutionResult>.Success(BatchExecutionResult.FromItems(items));
        }

        ExactMatchCandidateSet book = await _candidateQueries.GetOpenCandidatesAsync(
            organizationId,
            ExactMatchCandidateLimits.MaxPerSide,
            cancellationToken);

        if (book.InconsistentMoney)
        {
            return Result<BatchExecutionResult>.Failure(
                ReconciliationApplicationErrors.InconsistentMatchMoney);
        }

        if (book.InvoiceLimitExceeded || book.BankTransactionLimitExceeded)
        {
            Assign(items, identity, pending, 0, BatchExecutionItemOutcome.SkippedCoverage);
            return Result<BatchExecutionResult>.Success(BatchExecutionResult.FromItems(items));
        }

        for (int pendingIndex = 0; pendingIndex < pending.Count; pendingIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (now >= deadline)
            {
                Assign(
                    items,
                    identity,
                    pending,
                    pendingIndex,
                    BatchExecutionItemOutcome.Interrupted);
                break;
            }

            int index = pending[pendingIndex];
            Result<BatchExecutionItem> executed = await ExecuteNewItemAsync(
                organizationId,
                idempotencyKey,
                identity.Pairs[index],
                now,
                cancellationToken);

            if (executed.IsFailure)
            {
                return Result<BatchExecutionResult>.Failure(executed.Error);
            }

            items[index] = executed.Value;
        }

        return Result<BatchExecutionResult>.Success(BatchExecutionResult.FromItems(items));
    }

    private async Task<Result<BatchExecutionItem>?> TryRecoverAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        BatchExactMatchPair pair,
        CancellationToken cancellationToken)
    {
        ConfirmedAllocationRecord? existing = await _idempotencyLookup.FindAsync(
            organizationId,
            BatchExactMatchItemKey.Derive(
                organizationId,
                idempotencyKey,
                pair.InvoiceId,
                pair.BankTransactionId),
            cancellationToken);

        if (existing is null)
        {
            return null;
        }

        if (existing.IdempotencyOperation != AllocationIdempotencyOperation.BatchExactMatch
            || existing.InvoiceId != pair.InvoiceId
            || existing.BankTransactionId != pair.BankTransactionId)
        {
            return Result<BatchExecutionItem>.Failure(
                ReconciliationApplicationErrors.BatchAllocationIntegrityConflict);
        }

        return Result<BatchExecutionItem>.Success(ConfirmedItem(pair, existing));
    }

    private async Task<Result<BatchExecutionItem>> ExecuteNewItemAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        BatchExactMatchPair pair,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        BatchItemAttempt attempt;

        try
        {
            attempt = await ExecuteItemAsync(
                organizationId,
                idempotencyKey,
                pair,
                confirmedAt,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Result<BatchExecutionItem>.Failure(
                ReconciliationApplicationErrors.BatchInfrastructureFailure);
        }

        return attempt switch
        {
            BatchItemAttempt.Confirmed confirmed => Result<BatchExecutionItem>.Success(
                new BatchExecutionItem(
                    pair.InvoiceId,
                    pair.BankTransactionId,
                    BatchExecutionItemOutcome.Confirmed,
                    confirmed.AllocationId,
                    confirmed.Amount)),
            BatchItemAttempt.Rejected rejected => Result<BatchExecutionItem>.Success(
                RejectedItem(pair, rejected.Outcome)),
            BatchItemAttempt.Stopped stopped => Result<BatchExecutionItem>.Failure(stopped.Error),
            _ => Result<BatchExecutionItem>.Failure(
                ReconciliationApplicationErrors.BatchInfrastructureFailure)
        };
    }

    private static void Assign(
        BatchExecutionItem[] items,
        BatchExactMatchIdentity identity,
        List<int> pending,
        int fromPendingIndex,
        BatchExecutionItemOutcome outcome)
    {
        for (int index = fromPendingIndex; index < pending.Count; index++)
        {
            int itemIndex = pending[index];
            items[itemIndex] = RejectedItem(identity.Pairs[itemIndex], outcome);
        }
    }

    private async Task<Result<ExecuteBatchExactMatchesResponse>?> GateNewRequestAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        ExactMatchCandidateSet book = await _candidateQueries.GetOpenCandidatesAsync(
            organizationId,
            ExactMatchCandidateLimits.MaxPerSide,
            cancellationToken);

        if (book.InconsistentMoney)
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.InconsistentMatchMoney);
        }

        if (book.InvoiceLimitExceeded || book.BankTransactionLimitExceeded)
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchCandidateLimitExceeded);
        }

        return null;
    }

    private async Task<BatchExecutionSnapshot?> FindAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IBatchExecutionStore store = scope.ServiceProvider.GetRequiredService<IBatchExecutionStore>();
        return await store.FindAsync(organizationId, idempotencyKey, cancellationToken);
    }

    private async Task<bool> InsertAsync(
        BatchExecution execution,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IBatchExecutionStore store = scope.ServiceProvider.GetRequiredService<IBatchExecutionStore>();
        return await store.TryAddRunningAsync(execution, cancellationToken);
    }

    private async Task<BatchItemAttempt> ExecuteItemAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        BatchExactMatchPair pair,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IBatchExactMatchItemExecutor executor = scope.ServiceProvider
            .GetRequiredService<IBatchExactMatchItemExecutor>();
        return await executor.ExecuteAsync(
            organizationId,
            idempotencyKey,
            pair.InvoiceId,
            pair.BankTransactionId,
            confirmedAt,
            cancellationToken);
    }

    private static BatchExecutionItem ConfirmedItem(
        BatchExactMatchPair pair,
        ConfirmedAllocationRecord existing)
    {
        return new BatchExecutionItem(
            pair.InvoiceId,
            pair.BankTransactionId,
            BatchExecutionItemOutcome.Confirmed,
            existing.AllocationId,
            existing.Amount);
    }

    private static BatchExecutionItem RejectedItem(
        BatchExactMatchPair pair,
        BatchExecutionItemOutcome outcome)
    {
        return new BatchExecutionItem(
            pair.InvoiceId,
            pair.BankTransactionId,
            outcome,
            allocationId: null,
            amount: null);
    }

    private static Result<ExecuteBatchExactMatchesResponse> Stored(BatchExecutionSnapshot snapshot)
    {
        if (snapshot.Status != BatchExecutionStatus.Completed
            || snapshot.ResultPayload is null)
        {
            return Result<ExecuteBatchExactMatchesResponse>.Failure(
                ReconciliationApplicationErrors.BatchInfrastructureFailure);
        }

        BatchExecutionResult result = BatchExecutionResultDocument.Read(snapshot.ResultPayload);
        ExecuteBatchExactMatchItemResponse[] items =
            new ExecuteBatchExactMatchItemResponse[result.Items.Count];

        for (int index = 0; index < result.Items.Count; index++)
        {
            BatchExecutionItem item = result.Items[index];
            items[index] = new ExecuteBatchExactMatchItemResponse(
                item.InvoiceId.Value,
                item.BankTransactionId.Value,
                OutcomeName(item.Outcome),
                item.AllocationId?.Value,
                item.Amount?.Amount,
                item.Amount?.Currency.Value);
        }

        return Result<ExecuteBatchExactMatchesResponse>.Success(
            new ExecuteBatchExactMatchesResponse(
                result.ResultVersion,
                result.RequestedCount,
                result.ConfirmedCount,
                result.RejectedCount,
                result.InterruptedCount,
                result.AllConfirmed,
                result.HasRejections,
                result.Partial,
                items));
    }

    private static string OutcomeName(BatchExecutionItemOutcome outcome)
    {
        return outcome switch
        {
            BatchExecutionItemOutcome.Confirmed => "confirmed",
            BatchExecutionItemOutcome.SkippedAmbiguous => "skippedAmbiguous",
            BatchExecutionItemOutcome.SkippedCoverage => "skippedCoverage",
            BatchExecutionItemOutcome.SkippedReview => "skippedReview",
            BatchExecutionItemOutcome.Stale => "stale",
            BatchExecutionItemOutcome.Conflict => "conflict",
            BatchExecutionItemOutcome.NotFound => "notFound",
            BatchExecutionItemOutcome.Interrupted => "interrupted",
            _ => throw new InvalidOperationException("Batch execution item outcome is not defined.")
        };
    }
}