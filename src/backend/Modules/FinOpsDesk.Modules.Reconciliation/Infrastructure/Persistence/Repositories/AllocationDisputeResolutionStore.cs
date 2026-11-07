using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class AllocationDisputeResolutionStore :
    IAllocationDisputeResolutionLookup,
    IAllocationDisputeResolutionWriter
{
    private readonly ReconciliationDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public AllocationDisputeResolutionStore(
        ReconciliationDbContext dbContext,
        IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
    }

    public void Add(AllocationDisputeResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        _dbContext.AllocationDisputeResolutions.Add(resolution);
    }

    public Task<AllocationDisputeResolution?> FindByIdempotencyKeyAsync(
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

        return ReadAsync(
            (context, token) => context.AllocationDisputeResolutions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    resolution => resolution.OrganizationId == organizationId
                        && resolution.IdempotencyKey == idempotencyKey,
                    token),
            cancellationToken);
    }

    public Task<AllocationDisputeResolution?> FindByDisputeAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(disputeId);

        return ReadAsync(
            (context, token) => context.AllocationDisputeResolutions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    resolution => resolution.OrganizationId == organizationId
                        && resolution.AllocationDisputeId == disputeId,
                    token),
            cancellationToken);
    }

    public Task<bool> DisputeExistsAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(disputeId);

        return ReadAsync(
            (context, token) => context.AllocationDisputes
                .AsNoTracking()
                .AnyAsync(
                    dispute => dispute.OrganizationId == organizationId
                        && dispute.Id == disputeId,
                    token),
            cancellationToken);
    }

    private async Task<T> ReadAsync<T>(
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
}

internal sealed class AllocationDisputeResolutionConflict : IAllocationDisputeResolutionConflict
{
    public bool IsRecognizedUniqueConflict(Exception exception)
    {
        return RecognizeUnique(exception) is not null;
    }

    public bool IsDisputeForeignKeyConflict(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number == 547
                && sqlException.Message.Contains(
                    AllocationDisputeResolutionConfiguration.ForeignKeyName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static string? RecognizeUnique(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SqlException sqlException || sqlException.Number is not (2601 or 2627))
            {
                continue;
            }

            if (sqlException.Message.Contains(
                    AllocationDisputeResolutionConfiguration.IdempotencyKeyIndexName,
                    StringComparison.Ordinal))
            {
                return AllocationDisputeResolutionConfiguration.IdempotencyKeyIndexName;
            }

            if (sqlException.Message.Contains(
                    AllocationDisputeResolutionConfiguration.PrimaryKeyName,
                    StringComparison.Ordinal))
            {
                return AllocationDisputeResolutionConfiguration.PrimaryKeyName;
            }
        }

        return null;
    }
}