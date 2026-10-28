using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;

public sealed record GetAllocationAuditQuery(
    AllocationId AllocationId,
    int Page,
    int PageSize);

public sealed record FinancialAuditEntryResponse(
    Guid Id,
    Guid AllocationId,
    Guid InvoiceId,
    Guid BankTransactionId,
    Guid? BatchExecutionId,
    string Action,
    string Operation,
    string ActorId,
    MoneyDto Amount,
    DateTimeOffset RecordedAt);

public sealed record FinancialAuditListResponse(
    IReadOnlyList<FinancialAuditEntryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

public interface IFinancialAuditQueries
{
    Task<Result<FinancialAuditListResponse>> GetForAllocationAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}