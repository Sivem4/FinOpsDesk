using System.Data;
using System.Data.Common;

using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class AllocationDisputeStore :
    IAllocationDisputeLookup,
    IAllocationDisputeWriter
{
    private readonly ReconciliationDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public AllocationDisputeStore(
        ReconciliationDbContext dbContext,
        IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
    }

    public void Add(AllocationDispute dispute)
    {
        ArgumentNullException.ThrowIfNull(dispute);

        _dbContext.AllocationDisputes.Add(dispute);
    }

    public async Task<AllocationDispute?> FindByIdempotencyKeyAsync(
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

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();
        IFinancialCommitGuard guard = scope.ServiceProvider.GetRequiredService<IFinancialCommitGuard>();
        return await FinancialCommitRead.ExecuteAsync(
            context,
            guard,
            token => context.AllocationDisputes
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    dispute => dispute.OrganizationId == organizationId
                        && dispute.IdempotencyKey == idempotencyKey,
                    token),
            cancellationToken);
    }

    public async Task<IAllocationRowLock> LockAllocationAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        IDbContextTransaction transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
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
            if (value is null or DBNull)
            {
                return new AllocationRowLock(transaction, found: false, status: null);
            }

            int statusValue = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            AllocationStatus? status = Enum.IsDefined(typeof(AllocationStatus), statusValue)
                ? (AllocationStatus)statusValue
                : null;
            return new AllocationRowLock(transaction, found: true, status);
        }
        catch
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static async Task RollbackQuietlyAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class AllocationRowLock : IAllocationRowLock
    {
        private readonly IDbContextTransaction _transaction;
        private int _state;

        public AllocationRowLock(
            IDbContextTransaction transaction,
            bool found,
            AllocationStatus? status)
        {
            _transaction = transaction;
            Found = found;
            Status = status;
        }

        public bool Found { get; }

        public AllocationStatus? Status { get; }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _state) != 0)
            {
                throw new InvalidOperationException("The allocation lock is already closed.");
            }

            await _transaction.CommitAsync(cancellationToken);
            Interlocked.Exchange(ref _state, 1);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
            {
                return;
            }

            await _transaction.RollbackAsync(cancellationToken);
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

internal sealed class AllocationDisputeConflict : IAllocationDisputeConflict
{
    public bool IsIdempotencyKeyConflict(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number is 2601 or 2627
                && sqlException.Message.Contains(
                    AllocationDisputeConfiguration.IdempotencyKeyIndexName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}