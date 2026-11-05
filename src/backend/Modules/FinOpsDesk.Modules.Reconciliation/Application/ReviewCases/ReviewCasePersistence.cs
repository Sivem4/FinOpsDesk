using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public interface IReviewCaseRepository
{
    void Add(ReviewCase reviewCase);

    Task<ReviewCase?> GetByIdAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken);

    byte[] GetRowVersion(ReviewCase reviewCase);

    void ExpectRowVersion(ReviewCase reviewCase, byte[] rowVersion);
}

public interface IReviewCaseSnapshotLookup
{
    Task<ReviewCaseSnapshot?> FindOpenBySubjectAsync(
        OrganizationId organizationId,
        string subjectKey,
        CancellationToken cancellationToken);

    Task<ReviewCaseSnapshot?> FindByIdAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken);
}

public interface IReviewCaseOpenConflict
{
    bool IsOpenSubjectConflict(Exception exception);
}

public interface IReviewCaseOpenAttemptLookup
{
    Task<ReviewCaseOpenAttempt?> FindAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}

public interface IReviewCaseOpenAttemptWriter
{
    void Add(ReviewCaseOpenAttempt attempt);

    Task InsertAsync(ReviewCaseOpenAttempt attempt, CancellationToken cancellationToken);
}

public interface IReviewCaseOpenAttemptConflict
{
    bool IsPrimaryKeyConflict(Exception exception);
}

public interface IReviewCaseAuditConflict
{
    bool IsAuditUniquenessConflict(Exception exception);
}

public interface IAllocationLinkLookup
{
    Task<AllocationLink?> FindAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken);
}

public interface IReviewCaseQueries
{
    Task<ReviewCaseListResponse> ListAsync(
        OrganizationId organizationId,
        ReviewCaseStatus? status,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<Result<ReviewCaseDetailResponse>> GetAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken);
}