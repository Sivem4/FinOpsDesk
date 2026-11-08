using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class AllocationDisputeCorrectionQueries : IAllocationDisputeCorrectionQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public AllocationDisputeCorrectionQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<AllocationDisputeCorrectionStateResponse?> GetAsync(
        OrganizationId organizationId,
        Guid disputeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (disputeId == Guid.Empty)
        {
            return null;
        }

        AllocationDisputeId id = new(disputeId);
        bool exists = await _dbContext.AllocationDisputes
            .AsNoTracking()
            .AnyAsync(
                dispute => dispute.OrganizationId == organizationId && dispute.Id == id,
                cancellationToken);
        if (!exists)
        {
            return null;
        }

        AllocationDisputeCorrection? correction = await _dbContext.AllocationDisputeCorrections
            .AsNoTracking()
            .SingleOrDefaultAsync(
                stored => stored.OrganizationId == organizationId && stored.AllocationDisputeId == id,
                cancellationToken);
        if (correction is null)
        {
            return new AllocationDisputeCorrectionStateResponse(disputeId, null);
        }

        var allocation = await _dbContext.Allocations
            .AsNoTracking()
            .Where(stored => stored.OrganizationId == organizationId && stored.Id == correction.AllocationId)
            .Select(stored => new
            {
                stored.InvoiceId,
                stored.BankTransactionId,
                Amount = stored.Amount.Amount,
                Currency = stored.Amount.Currency,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (allocation is null)
        {
            throw new InvalidOperationException(
                "Stored allocation dispute correction does not match an allocation.");
        }

        return new AllocationDisputeCorrectionStateResponse(
            disputeId,
            new AllocationDisputeCorrectionView(
                disputeId,
                correction.AllocationId.Value,
                allocation.InvoiceId.Value,
                allocation.BankTransactionId.Value,
                correction.CorrectedAt,
                correction.Reason,
                ActorSourceName(correction.ActorSource),
                correction.ActorId,
                new MoneyDto(allocation.Amount, allocation.Currency.Value)));
    }

    private static string ActorSourceName(ReviewCaseActorSource source)
    {
        return source switch
        {
            ReviewCaseActorSource.DevelopmentJwt =>
                AllocationDispute.ActorSourceName(ReviewCaseActorSource.DevelopmentJwt),
            ReviewCaseActorSource.ProductionCookie =>
                AllocationDispute.ActorSourceName(ReviewCaseActorSource.ProductionCookie),
            _ => throw new InvalidOperationException("Stored correction actor source is not defined."),
        };
    }
}