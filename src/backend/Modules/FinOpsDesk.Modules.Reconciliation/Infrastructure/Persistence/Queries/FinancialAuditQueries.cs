using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class FinancialAuditQueries : IFinancialAuditQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public FinancialAuditQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<Result<FinancialAuditListResponse>> GetForAllocationAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        bool allocationExists = await _dbContext.Allocations
            .AsNoTracking()
            .AnyAsync(
                allocation => allocation.OrganizationId == organizationId
                    && allocation.Id == allocationId,
                cancellationToken);

        if (!allocationExists)
        {
            return Result<FinancialAuditListResponse>.Failure(
                ReconciliationApplicationErrors.AllocationNotFound);
        }

        IQueryable<FinancialAuditEntry> entries = _dbContext.FinancialAuditEntries
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId
                && entry.AllocationId == allocationId);

        int totalCount = await entries.CountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        List<AuditRow> rows = await entries
            .OrderBy(entry => entry.RecordedAt)
            .ThenBy(entry => entry.Id)
            .Skip(checked((int)offset))
            .Take(pageSize)
            .Select(entry => new AuditRow(
                entry.Id,
                entry.AllocationId,
                entry.InvoiceId,
                entry.BankTransactionId,
                entry.BatchExecutionId,
                entry.Action,
                entry.IdempotencyOperation,
                entry.ActorId,
                entry.Amount.Amount,
                entry.Amount.Currency.Value,
                entry.RecordedAt))
            .ToListAsync(cancellationToken);

        FinancialAuditEntryResponse[] items = new FinancialAuditEntryResponse[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            items[index] = ToResponse(rows[index]);
        }

        return Result<FinancialAuditListResponse>.Success(
            new FinancialAuditListResponse(items, page, pageSize, totalCount));
    }

    private static FinancialAuditEntryResponse ToResponse(AuditRow row)
    {
        return new FinancialAuditEntryResponse(
            row.Id.Value,
            row.AllocationId.Value,
            row.InvoiceId.Value,
            row.BankTransactionId.Value,
            row.BatchExecutionId?.Value,
            ActionName(row.Action),
            OperationName(row.Operation),
            row.ActorId,
            new MoneyDto(row.Amount, row.Currency),
            row.RecordedAt);
    }

    private static string ActionName(FinancialAuditAction action)
    {
        return action switch
        {
            FinancialAuditAction.Confirmation => "confirmation",
            _ => throw new InvalidOperationException(
                "Financial audit action is not defined."),
        };
    }

    private static string OperationName(AllocationIdempotencyOperation operation)
    {
        return operation switch
        {
            AllocationIdempotencyOperation.ExactMatch => "exactMatch",
            AllocationIdempotencyOperation.ExplicitAmount => "explicitAmount",
            AllocationIdempotencyOperation.BatchExactMatch => "batchExactMatch",
            _ => throw new InvalidOperationException(
                "Allocation idempotency operation is not defined."),
        };
    }

    private sealed record AuditRow(
        FinancialAuditEntryId Id,
        AllocationId AllocationId,
        InvoiceId InvoiceId,
        BankTransactionId BankTransactionId,
        BatchExecutionId? BatchExecutionId,
        FinancialAuditAction Action,
        AllocationIdempotencyOperation Operation,
        string ActorId,
        decimal Amount,
        string Currency,
        DateTimeOffset RecordedAt);
}