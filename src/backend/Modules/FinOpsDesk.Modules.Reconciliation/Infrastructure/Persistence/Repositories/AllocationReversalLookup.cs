using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class AllocationReversalLookup : IAllocationReversalLookup
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AllocationReversalLookup(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _scopeFactory = scopeFactory;
    }

    public async Task<AllocationReversalKeyRecord?> FindByKeyAsync(
        OrganizationId organizationId,
        Guid reversalIdempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (reversalIdempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Reversal idempotency key cannot be empty.",
                nameof(reversalIdempotencyKey));
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider
            .GetRequiredService<ReconciliationDbContext>();
        IFinancialCommitGuard guard = scope.ServiceProvider.GetRequiredService<IFinancialCommitGuard>();
        return await FinancialCommitRead.ExecuteAsync(context, guard, ReadAsync, cancellationToken);

        async Task<AllocationReversalKeyRecord?> ReadAsync(CancellationToken token)
        {
            Allocation? allocation = await context.Allocations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    stored => stored.OrganizationId == organizationId
                        && EF.Property<Guid?>(
                            stored,
                            AllocationConfiguration.ReversalIdempotencyKeyPropertyName) == reversalIdempotencyKey,
                    token);

            if (allocation is null)
            {
                return null;
            }

            List<FinancialAuditEntry> audits = await context.FinancialAuditEntries
                .AsNoTracking()
                .Where(entry => entry.OrganizationId == organizationId
                    && entry.AllocationId == allocation.Id
                    && entry.Action == FinancialAuditAction.Reversal)
                .ToListAsync(token);

            AllocationReversalAuditRecord? audit = audits.Count == 1
                ? new AllocationReversalAuditRecord(
                    audits[0].AllocationId,
                    audits[0].InvoiceId,
                    audits[0].BankTransactionId,
                    audits[0].Action,
                    audits[0].IdempotencyOperation,
                    audits[0].IdempotencyKey,
                    audits[0].Amount,
                    audits[0].RecordedAt,
                    audits[0].Reason)
                : null;

            return new AllocationReversalKeyRecord(
                allocation.Id,
                allocation.InvoiceId,
                allocation.BankTransactionId,
                allocation.Status,
                allocation.ReversalReason,
                allocation.ReversedAt,
                allocation.Amount,
                reversalIdempotencyKey,
                audits.Count,
                audit);
        }
    }

    public async Task<AllocationReversalState?> FindAllocationStateAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ReconciliationDbContext context = scope.ServiceProvider
            .GetRequiredService<ReconciliationDbContext>();

        var row = await context.Allocations
            .AsNoTracking()
            .Where(allocation => allocation.OrganizationId == organizationId
                && allocation.Id == allocationId)
            .Select(allocation => new { allocation.Status })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : new AllocationReversalState(row.Status);
    }
}