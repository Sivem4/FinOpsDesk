using System.Data;
using System.Data.Common;

using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class AllocationDisputeCorrectionStore :
    IAllocationDisputeCorrectionReads,
    IAllocationDisputeCorrectionWork
{
    private readonly ReconciliationDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public AllocationDisputeCorrectionStore(
        ReconciliationDbContext dbContext,
        IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
    }

    public Task<AllocationDisputeCorrectionLink?> FindCorrectionByKeyAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        return FreshAsync(
            (context, token) => CorrectionQueries.FindByKeyAsync(context, organizationId, idempotencyKey, token),
            cancellationToken);
    }

    public Task<AllocationReversalFacts?> FindReversalByKeyAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        return FreshAsync(
            (context, token) => CorrectionQueries.FindReversalByKeyAsync(context, organizationId, idempotencyKey, token),
            cancellationToken);
    }

    public Task<AllocationDisputeCorrectionLink?> FindCorrectionByDisputeAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        return FreshAsync(
            (context, token) => CorrectionQueries.FindByDisputeAsync(context, organizationId, disputeId, token),
            cancellationToken);
    }

    public Task<AllocationReversalFacts?> FindAllocationAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        return FreshAsync(
            (context, token) => CorrectionQueries.FindAllocationAsync(context, organizationId, allocationId, token),
            cancellationToken);
    }

    public Task<IReadOnlyList<AllocationReversalAuditFact>> ListReversalAuditsAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        return FreshAsync(
            (context, token) => CorrectionQueries.ListReversalAuditsAsync(context, organizationId, allocationId, token),
            cancellationToken);
    }

    public Task<AllocationDisputeTarget?> FindDisputeAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        return FreshAsync(
            (context, token) => CorrectionQueries.FindDisputeAsync(context, organizationId, disputeId, token),
            cancellationToken);
    }

    public Task<AllocationDisputeResolutionOutcome?> FindResolutionOutcomeAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        return FreshAsync(
            (context, token) => CorrectionQueries.FindResolutionOutcomeAsync(context, organizationId, disputeId, token),
            cancellationToken);
    }

    public async Task<IAllocationDisputeCorrectionSession> BeginAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        IDbContextTransaction transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            bool found = await LockAllocationAsync(transaction, organizationId, allocationId, cancellationToken);
            return new CorrectionSession(_dbContext, transaction, found);
        }
        catch
        {
            await RollbackQuietlyAsync(transaction);
            await transaction.DisposeAsync();
            throw;
        }
    }

    private async Task<T> FreshAsync<T>(
        Func<ReconciliationDbContext, CancellationToken, Task<T>> query,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();
        IFinancialCommitGuard guard = scope.ServiceProvider.GetRequiredService<IFinancialCommitGuard>();
        return await FinancialCommitRead.ExecuteAsync(
            context,
            guard,
            token => query(context, token),
            cancellationToken);
    }

    private async Task<bool> LockAllocationAsync(
        IDbContextTransaction transaction,
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        DbConnection connection = _dbContext.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            SELECT [Status]
            FROM [reconciliation].[Allocations] WITH (UPDLOCK, ROWLOCK)
            WHERE [OrganizationId] = @organizationId AND [Id] = @allocationId
            """;
        command.Parameters.Add(new SqlParameter("@organizationId", SqlDbType.UniqueIdentifier)
        {
            Value = organizationId.Value,
        });
        command.Parameters.Add(new SqlParameter("@allocationId", SqlDbType.UniqueIdentifier)
        {
            Value = allocationId.Value,
        });
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null and not DBNull;
    }

    private static async Task RollbackQuietlyAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class CorrectionSession : IAllocationDisputeCorrectionSession
    {
        private readonly ReconciliationDbContext _dbContext;
        private readonly IDbContextTransaction _transaction;
        private int _state;

        public CorrectionSession(
            ReconciliationDbContext dbContext,
            IDbContextTransaction transaction,
            bool allocationFound)
        {
            _dbContext = dbContext;
            _transaction = transaction;
            AllocationFound = allocationFound;
        }

        public bool AllocationFound { get; }

        public void Add(AllocationDisputeCorrection correction)
        {
            ArgumentNullException.ThrowIfNull(correction);
            _dbContext.AllocationDisputeCorrections.Add(correction);
        }

        public Task<AllocationDisputeCorrectionLink?> FindCorrectionByKeyAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            return CorrectionQueries.FindByKeyAsync(_dbContext, organizationId, idempotencyKey, cancellationToken);
        }

        public Task<AllocationReversalFacts?> FindReversalByKeyAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            return CorrectionQueries.FindReversalByKeyAsync(_dbContext, organizationId, idempotencyKey, cancellationToken);
        }

        public Task<AllocationDisputeCorrectionLink?> FindCorrectionByDisputeAsync(
            OrganizationId organizationId,
            AllocationDisputeId disputeId,
            CancellationToken cancellationToken)
        {
            return CorrectionQueries.FindByDisputeAsync(_dbContext, organizationId, disputeId, cancellationToken);
        }

        public Task<AllocationReversalFacts?> FindAllocationAsync(
            OrganizationId organizationId,
            AllocationId allocationId,
            CancellationToken cancellationToken)
        {
            return CorrectionQueries.FindAllocationAsync(_dbContext, organizationId, allocationId, cancellationToken);
        }

        public Task<IReadOnlyList<AllocationReversalAuditFact>> ListReversalAuditsAsync(
            OrganizationId organizationId,
            AllocationId allocationId,
            CancellationToken cancellationToken)
        {
            return CorrectionQueries.ListReversalAuditsAsync(_dbContext, organizationId, allocationId, cancellationToken);
        }

        public Task<AllocationDisputeTarget?> FindDisputeAsync(
            OrganizationId organizationId,
            AllocationDisputeId disputeId,
            CancellationToken cancellationToken)
        {
            return CorrectionQueries.FindDisputeAsync(_dbContext, organizationId, disputeId, cancellationToken);
        }

        public Task<AllocationDisputeResolutionOutcome?> FindResolutionOutcomeAsync(
            OrganizationId organizationId,
            AllocationDisputeId disputeId,
            CancellationToken cancellationToken)
        {
            return CorrectionQueries.FindResolutionOutcomeAsync(_dbContext, organizationId, disputeId, cancellationToken);
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _state) != 0)
            {
                throw new InvalidOperationException("The correction transaction is already closed.");
            }

            await _transaction.CommitAsync(cancellationToken);
            Interlocked.Exchange(ref _state, 1);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _state) != 0)
            {
                return;
            }

            await _transaction.RollbackAsync(cancellationToken);
            Interlocked.Exchange(ref _state, 2);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                try
                {
                    await _transaction.RollbackAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                }
            }

            await _transaction.DisposeAsync();
        }
    }
}

internal static class CorrectionQueries
{
    public static async Task<AllocationDisputeCorrectionLink?> FindByKeyAsync(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));
        }

        AllocationDisputeCorrection? stored = await context.AllocationDisputeCorrections
            .AsNoTracking()
            .SingleOrDefaultAsync(
                correction => correction.OrganizationId == organizationId
                    && correction.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return stored is null ? null : ToLink(stored);
    }

    public static async Task<AllocationDisputeCorrectionLink?> FindByDisputeAsync(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(disputeId);

        AllocationDisputeCorrection? stored = await context.AllocationDisputeCorrections
            .AsNoTracking()
            .SingleOrDefaultAsync(
                correction => correction.OrganizationId == organizationId
                    && correction.AllocationDisputeId == disputeId,
                cancellationToken);
        return stored is null ? null : ToLink(stored);
    }

    public static async Task<AllocationReversalFacts?> FindReversalByKeyAsync(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));
        }

        return await ProjectAllocationAsync(
            context.Allocations.AsNoTracking().Where(stored => stored.OrganizationId == organizationId
                && EF.Property<Guid?>(stored, AllocationConfiguration.ReversalIdempotencyKeyPropertyName) == idempotencyKey),
            cancellationToken);
    }

    public static async Task<AllocationReversalFacts?> FindAllocationAsync(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        return await ProjectAllocationAsync(
            context.Allocations.AsNoTracking().Where(stored => stored.OrganizationId == organizationId
                && stored.Id == allocationId),
            cancellationToken);
    }

    public static async Task<IReadOnlyList<AllocationReversalAuditFact>> ListReversalAuditsAsync(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId
                && entry.AllocationId == allocationId
                && entry.Action == FinancialAuditAction.Reversal)
            .ToListAsync(cancellationToken);
        return audits.Select(entry => new AllocationReversalAuditFact(
                entry.AllocationId,
                entry.InvoiceId,
                entry.BankTransactionId,
                entry.Action,
                entry.IdempotencyOperation,
                entry.IdempotencyKey,
                entry.Amount,
                entry.RecordedAt,
                entry.Reason,
                entry.ActorId))
            .ToArray();
    }

    public static async Task<AllocationDisputeTarget?> FindDisputeAsync(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(disputeId);

        AllocationId? allocationId = await context.AllocationDisputes
            .AsNoTracking()
            .Where(dispute => dispute.OrganizationId == organizationId && dispute.Id == disputeId)
            .Select(dispute => (AllocationId?)dispute.AllocationId)
            .SingleOrDefaultAsync(cancellationToken);
        return allocationId is null ? null : new AllocationDisputeTarget(allocationId);
    }

    public static async Task<AllocationDisputeResolutionOutcome?> FindResolutionOutcomeAsync(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(disputeId);

        AllocationDisputeResolutionOutcome? outcome = await context.AllocationDisputeResolutions
            .AsNoTracking()
            .Where(resolution => resolution.OrganizationId == organizationId
                && resolution.AllocationDisputeId == disputeId)
            .Select(resolution => (AllocationDisputeResolutionOutcome?)resolution.Outcome)
            .SingleOrDefaultAsync(cancellationToken);
        return outcome;
    }

    private static AllocationDisputeCorrectionLink ToLink(AllocationDisputeCorrection stored)
    {
        return new AllocationDisputeCorrectionLink(
            stored.AllocationDisputeId,
            stored.AllocationId,
            stored.IdempotencyKey,
            stored.Reason,
            stored.ActorId,
            stored.ActorSource,
            stored.CorrectedAt);
    }

    private static async Task<AllocationReversalFacts?> ProjectAllocationAsync(
        IQueryable<Allocation> query,
        CancellationToken cancellationToken)
    {
        var row = await query
            .Select(stored => new
            {
                stored.Id,
                stored.InvoiceId,
                stored.BankTransactionId,
                stored.Status,
                Amount = stored.Amount.Amount,
                Currency = stored.Amount.Currency,
                stored.ReversalReason,
                stored.ReversedAt,
                ReversalKey = EF.Property<Guid?>(stored, AllocationConfiguration.ReversalIdempotencyKeyPropertyName),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        return new AllocationReversalFacts(
            row.Id,
            row.InvoiceId,
            row.BankTransactionId,
            row.Status,
            new Money(row.Amount, row.Currency),
            row.ReversalReason,
            row.ReversedAt,
            row.ReversalKey);
    }
}

internal sealed class AllocationDisputeCorrectionConflict : IAllocationDisputeCorrectionConflict
{
    public bool IsRecognizedUniqueConflict(Exception exception)
    {
        return SqlServerUniqueIndexes.IsViolation(
                exception,
                AllocationConfiguration.ReversalIdempotencyIndexName)
            || SqlServerUniqueIndexes.IsViolation(
                exception,
                AllocationDisputeCorrectionConfiguration.IdempotencyKeyIndexName)
            || SqlServerUniqueIndexes.IsViolation(
                exception,
                AllocationDisputeCorrectionConfiguration.PrimaryKeyName);
    }

    public bool IsDisputeOrAllocationForeignKey(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number == 547
                && (sqlException.Message.Contains(
                        AllocationDisputeCorrectionConfiguration.DisputeForeignKeyName,
                        StringComparison.Ordinal)
                    || sqlException.Message.Contains(
                        AllocationDisputeCorrectionConfiguration.AllocationForeignKeyName,
                        StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}