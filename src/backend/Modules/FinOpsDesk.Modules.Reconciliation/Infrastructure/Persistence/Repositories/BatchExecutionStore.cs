using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class BatchExecutionStore : IBatchExecutionStore
{
    private readonly ReconciliationDbContext _dbContext;

    public BatchExecutionStore(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<BatchExecutionSnapshot?> FindAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        BatchExecution? execution = await _dbContext.BatchExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                stored => stored.OrganizationId == organizationId
                    && stored.IdempotencyKey == idempotencyKey,
                cancellationToken);

        return execution is null ? null : ToSnapshot(execution);
    }

    public async Task AddRunningAsync(
        BatchExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        EnsureRunning(execution);
        _dbContext.BatchExecutions.Add(execution);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new UniqueConstraintConflictException(
                "A unique constraint rejected the batch execution.",
                exception);
        }

        _dbContext.Entry(execution).State = EntityState.Detached;
    }

    public async Task<bool> TryAddRunningAsync(
        BatchExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        EnsureRunning(execution);
        _dbContext.BatchExecutions.Add(execution);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlServerUniqueIndexes.IsViolation(
            exception,
            BatchExecutionConfiguration.IdempotencyIndexName))
        {
            return false;
        }

        _dbContext.Entry(execution).State = EntityState.Detached;
        return true;
    }

    public async Task<bool> TryCompleteAsync(
        BatchExecutionId id,
        OrganizationId organizationId,
        BatchExecutionResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(result);

        if (completedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                "Batch execution completion time cannot be the default value.");
        }

        BatchExecution? current = await _dbContext.BatchExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                stored => stored.OrganizationId == organizationId && stored.Id == id,
                cancellationToken);

        if (current is null)
        {
            throw new InvalidOperationException("Batch execution was not found.");
        }

        if (current.Status == BatchExecutionStatus.Completed)
        {
            return false;
        }

        if (completedAt < current.CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                "Batch execution completion time cannot precede creation time.");
        }

        BatchExecution.EnsureResultMatches(current.Fingerprint, result);

        string payload = BatchExecutionResultDocument.Write(result);
        int running = BatchExecutionStatusCodec.ToInt(BatchExecutionStatus.Running);
        int completed = BatchExecutionStatusCodec.ToInt(BatchExecutionStatus.Completed);
        int updated = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [reconciliation].[BatchExecutions]
            SET [Status] = {completed},
                [ResultVersion] = {result.ResultVersion},
                [ResultPayload] = {payload},
                [CompletedAt] = {completedAt}
            WHERE [Id] = {id.Value}
                AND [OrganizationId] = {organizationId.Value}
                AND [Status] = {running}
                AND [ResultVersion] IS NULL
                AND [ResultPayload] IS NULL
                AND [CompletedAt] IS NULL
                AND {completedAt} >= [CreatedAt]
            """,
            cancellationToken);

        return updated == 1;
    }

    private static void EnsureRunning(BatchExecution execution)
    {
        if (execution.Status != BatchExecutionStatus.Running
            || execution.ResultVersion is not null
            || execution.ResultPayload is not null
            || execution.CompletedAt is not null)
        {
            throw new ArgumentException(
                "Only a running batch execution can be inserted.",
                nameof(execution));
        }
    }

    private static BatchExecutionSnapshot ToSnapshot(BatchExecution execution)
    {
        return new BatchExecutionSnapshot(
            execution.Id,
            execution.OrganizationId,
            execution.IdempotencyKey,
            execution.Fingerprint,
            execution.Status,
            execution.ResultVersion,
            execution.ResultPayload,
            execution.CreatedAt,
            execution.CompletedAt);
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException.Number is 2601 or 2627;
            }
        }

        return false;
    }
}