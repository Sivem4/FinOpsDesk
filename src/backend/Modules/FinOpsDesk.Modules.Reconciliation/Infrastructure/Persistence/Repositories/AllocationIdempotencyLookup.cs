using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class AllocationIdempotencyLookup : IAllocationIdempotencyLookup
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AllocationIdempotencyLookup(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _scopeFactory = scopeFactory;
    }

    public async Task<ConfirmedAllocationRecord?> FindAsync(
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
        ReconciliationDbContext context = scope.ServiceProvider
            .GetRequiredService<ReconciliationDbContext>();
        IFinancialCommitGuard guard = scope.ServiceProvider.GetRequiredService<IFinancialCommitGuard>();

        return await FinancialCommitRead.ExecuteAsync(
            context,
            guard,
            async token =>
            {
                Allocation? allocation = await context.Allocations
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        stored => stored.OrganizationId == organizationId
                            && stored.IdempotencyKey == idempotencyKey,
                        token);

                if (allocation is null)
                {
                    return null;
                }

                return new ConfirmedAllocationRecord(
                    allocation.Id,
                    allocation.InvoiceId,
                    allocation.BankTransactionId,
                    allocation.Amount,
                    allocation.IdempotencyOperation);
            },
            cancellationToken);
    }
}