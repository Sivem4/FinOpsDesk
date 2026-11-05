using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.ReviewCases;

internal static class ReviewCaseHandlerTestDoubles
{
    public static readonly DateTimeOffset OpenedAt = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static readonly CurrencyCode Pln = new("PLN");

    public static readonly byte[] CurrentVersion = [1, 2, 3, 4, 5, 6, 7, 8];

    public static readonly byte[] OtherVersion = [8, 7, 6, 5, 4, 3, 2, 1];

    public static Invoice CreateInvoice(OrganizationId organizationId)
    {
        return new Invoice(
            InvoiceId.New(),
            organizationId,
            "INV-001",
            new Money(100m, Pln));
    }

    public static BankTransaction CreateBankTransaction(OrganizationId organizationId)
    {
        return new BankTransaction(
            BankTransactionId.New(),
            organizationId,
            new Money(100m, Pln),
            new DateOnly(2026, 10, 26),
            "INV-001");
    }

    public static ReviewCaseSnapshot Snapshot(ReviewCase reviewCase, byte[] rowVersion)
    {
        return new ReviewCaseSnapshot(
            reviewCase.Id,
            reviewCase.OrganizationId,
            reviewCase.Reason,
            reviewCase.Status,
            reviewCase.InvoiceId,
            reviewCase.BankTransactionId,
            reviewCase.SubjectKey,
            reviewCase.SourceLabel,
            reviewCase.Note,
            reviewCase.Outcome,
            reviewCase.ResolutionNote,
            reviewCase.AllocationId,
            reviewCase.OpenedAt,
            reviewCase.ClosedAt,
            rowVersion.ToArray());
    }
}

internal sealed class RecordingOrganizationContext : ICurrentOrganizationContext
{
    private readonly OrganizationId _organizationId;

    public RecordingOrganizationContext(OrganizationId organizationId)
    {
        _organizationId = organizationId;
    }

    public int ReadCount { get; private set; }

    public OrganizationId OrganizationId
    {
        get
        {
            ReadCount++;
            return _organizationId;
        }
    }
}

internal sealed class ThrowingOrganizationContext : ICurrentOrganizationContext
{
    public OrganizationId OrganizationId =>
        throw new InvalidOperationException(
            "A valid organization claim is required on the authenticated user.");
}

internal sealed class FixedTimeProvider : TimeProvider
{
    public FixedTimeProvider(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; }

    public int ReadCount { get; private set; }

    public override DateTimeOffset GetUtcNow()
    {
        ReadCount++;
        return UtcNow;
    }
}

internal sealed class FixedActorContext : ICurrentActorContext
{
    private readonly string? _actorId;

    public FixedActorContext(string? actorId = "operator-a")
    {
        _actorId = actorId;
    }

    public bool TryGetActorId(out string actorId)
    {
        if (string.IsNullOrWhiteSpace(_actorId)
            || _actorId != _actorId.Trim()
            || _actorId.Length > ReviewCaseAuditEntry.MaxActorIdLength)
        {
            actorId = string.Empty;
            return false;
        }

        actorId = _actorId;
        return true;
    }
}

internal sealed class NoReviewCaseAuditConflict : IReviewCaseAuditConflict
{
    public bool IsAuditUniquenessConflict(Exception exception)
    {
        return false;
    }
}

internal sealed class RecordingReviewCaseAuditWriter : IReviewCaseAuditWriter
{
    public List<ReviewCaseAuditEntry> Entries { get; } = [];

    public void AppendOpened(ReviewCase reviewCase, string actorId)
    {
        Entries.Add(ReviewCaseAuditEntry.ForOpened(ReviewCaseAuditEntryId.New(), reviewCase, actorId));
    }

    public void AppendResolved(ReviewCase reviewCase, string actorId)
    {
        Entries.Add(ReviewCaseAuditEntry.ForResolved(ReviewCaseAuditEntryId.New(), reviewCase, actorId));
    }

    public void AppendDismissed(ReviewCase reviewCase, string actorId)
    {
        Entries.Add(ReviewCaseAuditEntry.ForDismissed(ReviewCaseAuditEntryId.New(), reviewCase, actorId));
    }
}

internal sealed class ScriptedUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Exception? Exception { get; set; }

    public CancellationToken CancellationToken { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        SaveCount++;
        if (Exception is not null)
        {
            Exception exception = Exception;
            Exception = null;
            throw exception;
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeInvoiceRepository : IInvoiceRepository
{
    private readonly Dictionary<(OrganizationId OrganizationId, InvoiceId InvoiceId), Invoice> _invoices = [];

    public int CallCount { get; private set; }

    public OrganizationId? OrganizationId { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public void Add(Invoice invoice)
    {
        _invoices[(invoice.OrganizationId, invoice.Id)] = invoice;
    }

    public Task<Invoice?> GetByIdAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        CallCount++;
        OrganizationId = organizationId;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        _invoices.TryGetValue((organizationId, invoiceId), out Invoice? invoice);
        return Task.FromResult(invoice);
    }
}

internal sealed class FakeBankTransactionRepository : IBankTransactionRepository
{
    private readonly Dictionary<(OrganizationId OrganizationId, BankTransactionId BankTransactionId), BankTransaction> _bankTransactions = [];

    public int CallCount { get; private set; }

    public OrganizationId? OrganizationId { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public void Add(BankTransaction bankTransaction)
    {
        _bankTransactions[(bankTransaction.OrganizationId, bankTransaction.Id)] = bankTransaction;
    }

    public Task<BankTransaction?> GetByIdAsync(
        OrganizationId organizationId,
        BankTransactionId bankTransactionId,
        CancellationToken cancellationToken)
    {
        CallCount++;
        OrganizationId = organizationId;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        _bankTransactions.TryGetValue((organizationId, bankTransactionId), out BankTransaction? bankTransaction);
        return Task.FromResult(bankTransaction);
    }
}

internal sealed class FakeReviewCaseRepository : IReviewCaseRepository
{
    private readonly Dictionary<Guid, ReviewCase> _cases = [];
    private readonly Dictionary<Guid, byte[]> _versions = [];

    public int AddCount { get; private set; }

    public int GetCount { get; private set; }

    public OrganizationId? OrganizationId { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public byte[]? ExpectedVersion { get; private set; }

    public void Seed(ReviewCase reviewCase, byte[] rowVersion)
    {
        _cases[reviewCase.Id.Value] = reviewCase;
        _versions[reviewCase.Id.Value] = rowVersion.ToArray();
    }

    public void Add(ReviewCase reviewCase)
    {
        AddCount++;
        _cases[reviewCase.Id.Value] = reviewCase;
        _versions[reviewCase.Id.Value] = ReviewCaseHandlerTestDoubles.CurrentVersion.ToArray();
    }

    public Task<ReviewCase?> GetByIdAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken)
    {
        GetCount++;
        OrganizationId = organizationId;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (!_cases.TryGetValue(reviewCaseId.Value, out ReviewCase? reviewCase)
            || reviewCase.OrganizationId != organizationId)
        {
            return Task.FromResult<ReviewCase?>(null);
        }

        return Task.FromResult<ReviewCase?>(reviewCase);
    }

    public byte[] GetRowVersion(ReviewCase reviewCase)
    {
        return _versions[reviewCase.Id.Value].ToArray();
    }

    public void ExpectRowVersion(ReviewCase reviewCase, byte[] rowVersion)
    {
        ExpectedVersion = rowVersion.ToArray();
    }
}

internal sealed class FakeReviewCaseSnapshotLookup : IReviewCaseSnapshotLookup
{
    public ReviewCaseSnapshot? OpenSnapshot { get; set; }

    public ReviewCaseSnapshot? ById { get; set; }

    public int OpenLookups { get; private set; }

    public int IdLookups { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public Task<ReviewCaseSnapshot?> FindOpenBySubjectAsync(
        OrganizationId organizationId,
        string subjectKey,
        CancellationToken cancellationToken)
    {
        OpenLookups++;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (OpenSnapshot is null
            || OpenSnapshot.OrganizationId != organizationId
            || OpenSnapshot.SubjectKey != subjectKey
            || OpenSnapshot.Status != ReviewCaseStatus.Open)
        {
            return Task.FromResult<ReviewCaseSnapshot?>(null);
        }

        return Task.FromResult<ReviewCaseSnapshot?>(OpenSnapshot);
    }

    public Task<ReviewCaseSnapshot?> FindByIdAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken)
    {
        IdLookups++;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (ById is null
            || ById.OrganizationId != organizationId
            || ById.ReviewCaseId != reviewCaseId)
        {
            return Task.FromResult<ReviewCaseSnapshot?>(null);
        }

        return Task.FromResult<ReviewCaseSnapshot?>(ById);
    }
}

internal sealed class FakeReviewCaseOpenAttempts :
    IReviewCaseOpenAttemptLookup,
    IReviewCaseOpenAttemptWriter,
    IReviewCaseOpenAttemptConflict
{
    private readonly List<ReviewCaseOpenAttempt> _stored = [];

    public int FindCount { get; private set; }

    public int AddCount { get; private set; }

    public int InsertCount { get; private set; }

    public bool ClassifyPrimaryKey { get; set; }

    public bool ThrowPrimaryKeyOnInsert { get; set; }

    public List<ReviewCaseOpenAttempt> Added { get; } = [];

    public Func<int, ReviewCaseOpenAttempt?>? FindScript { get; set; }

    public Task<ReviewCaseOpenAttempt?> FindAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        FindCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (FindScript is not null)
        {
            return Task.FromResult(FindScript(FindCount));
        }

        return Task.FromResult(_stored.SingleOrDefault(attempt =>
            attempt.OrganizationId == organizationId
            && attempt.IdempotencyKey == idempotencyKey));
    }

    public void Add(ReviewCaseOpenAttempt attempt)
    {
        AddCount++;
        Added.Add(attempt);
        _stored.Add(attempt);
    }

    public Task InsertAsync(ReviewCaseOpenAttempt attempt, CancellationToken cancellationToken)
    {
        InsertCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowPrimaryKeyOnInsert)
        {
            throw new UniqueConstraintConflictException("primary key");
        }

        _stored.Add(attempt);
        return Task.CompletedTask;
    }

    public bool IsPrimaryKeyConflict(Exception exception)
    {
        return ClassifyPrimaryKey;
    }
}

internal sealed class FakeReviewCaseOpenConflict : IReviewCaseOpenConflict
{
    public bool Conflict { get; set; }

    public bool IsOpenSubjectConflict(Exception exception)
    {
        return Conflict;
    }
}

internal sealed class FakeAllocationLinkLookup : IAllocationLinkLookup
{
    public int CallCount { get; private set; }

    public OrganizationId? OrganizationId { get; private set; }

    public AllocationLink? Link { get; set; }

    public OrganizationId? OwnerOrganizationId { get; set; }

    public CancellationToken CancellationToken { get; private set; }

    public Task<AllocationLink?> FindAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        CallCount++;
        OrganizationId = organizationId;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (Link is null
            || Link.AllocationId != allocationId
            || (OwnerOrganizationId is not null && OwnerOrganizationId != organizationId))
        {
            return Task.FromResult<AllocationLink?>(null);
        }

        return Task.FromResult<AllocationLink?>(Link);
    }
}

internal sealed class FakeReviewCaseQueries : IReviewCaseQueries
{
    private readonly ReviewCaseListResponse _list;
    private readonly ReviewCaseDetailResponse? _detail;

    public FakeReviewCaseQueries(
        ReviewCaseListResponse? list = null,
        ReviewCaseDetailResponse? detail = null)
    {
        _list = list ?? new ReviewCaseListResponse([], 1, 50, 0);
        _detail = detail;
    }

    public int ListCount { get; private set; }

    public int GetCount { get; private set; }

    public OrganizationId? OrganizationId { get; private set; }

    public ReviewCaseStatus? Status { get; private set; }

    public InvoiceId? InvoiceId { get; private set; }

    public BankTransactionId? BankTransactionId { get; private set; }

    public int Page { get; private set; }

    public int PageSize { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public Task<ReviewCaseListResponse> ListAsync(
        OrganizationId organizationId,
        ReviewCaseStatus? status,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ListCount++;
        OrganizationId = organizationId;
        Status = status;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        Page = page;
        PageSize = pageSize;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_list);
    }

    public Task<Result<ReviewCaseDetailResponse>> GetAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken)
    {
        GetCount++;
        OrganizationId = organizationId;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (_detail is null || _detail.ReviewCase.ReviewCaseId != reviewCaseId.Value)
        {
            return Task.FromResult(
                Result<ReviewCaseDetailResponse>.Failure(
                    ReconciliationApplicationErrors.ReviewCaseNotFound));
        }

        return Task.FromResult(Result<ReviewCaseDetailResponse>.Success(_detail));
    }
}