using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed record OpenReviewCaseCommand(
    ReviewCaseReason Reason,
    InvoiceId? InvoiceId,
    BankTransactionId? BankTransactionId,
    string? SourceLabel,
    string? Note,
    Guid? IdempotencyKey = null);

public sealed record ResolveReviewCaseCommand(
    ReviewCaseId ReviewCaseId,
    ReviewCaseOutcome Outcome,
    AllocationId? AllocationId,
    string? ResolutionNote,
    string ConcurrencyToken);

public sealed record DismissReviewCaseCommand(
    ReviewCaseId ReviewCaseId,
    string? ResolutionNote,
    string ConcurrencyToken);

public sealed record ListReviewCasesQuery(
    ReviewCaseStatus? Status,
    InvoiceId? InvoiceId,
    BankTransactionId? BankTransactionId,
    int Page,
    int PageSize);

public sealed record GetReviewCaseQuery(ReviewCaseId ReviewCaseId);

public sealed record ReviewCaseResponse(
    Guid ReviewCaseId,
    string Reason,
    string Status,
    Guid? InvoiceId,
    Guid? BankTransactionId,
    string? SourceLabel,
    string? Note,
    string? Outcome,
    string? ResolutionNote,
    Guid? AllocationId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string ConcurrencyToken);

public sealed record OpenReviewCaseResponse(
    ReviewCaseResponse ReviewCase,
    bool Created);

public sealed record ReviewCaseLinkedFinancials(
    bool InvoiceFound,
    MoneyDto? InvoiceOutstanding,
    bool BankTransactionFound,
    MoneyDto? BankTransactionUnapplied);

public sealed record ReviewCaseDetailResponse(
    ReviewCaseResponse ReviewCase,
    ReviewCaseLinkedFinancials LinkedFinancials);

public sealed record ReviewCaseListResponse(
    IReadOnlyList<ReviewCaseResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record AllocationLink(
    AllocationId AllocationId,
    InvoiceId InvoiceId,
    BankTransactionId BankTransactionId,
    AllocationStatus Status);

public sealed record ReviewCaseSnapshot(
    ReviewCaseId ReviewCaseId,
    OrganizationId OrganizationId,
    ReviewCaseReason Reason,
    ReviewCaseStatus Status,
    InvoiceId? InvoiceId,
    BankTransactionId? BankTransactionId,
    string SubjectKey,
    ReviewCaseSource? SourceLabel,
    string? Note,
    ReviewCaseOutcome? Outcome,
    string? ResolutionNote,
    AllocationId? AllocationId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    byte[] RowVersion);