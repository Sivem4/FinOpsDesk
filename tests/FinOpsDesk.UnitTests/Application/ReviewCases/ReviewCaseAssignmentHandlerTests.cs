using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.ReviewCases;

public sealed class ReviewCaseAssignmentHandlerTests
{
    private static readonly DateTimeOffset Opened = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Token = [1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly string Canonical = Convert.ToBase64String(Token);

    [Fact]
    public async Task Missing_actor_is_rejected_before_a_stored_key_is_read()
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseId reviewCaseId = ReviewCaseId.New();
        Guid key = Guid.NewGuid();
        ScriptedLookup lookup = new(
            Entry(organizationId, reviewCaseId, ReviewCaseAssignmentOperation.Claim, ReviewCaseActorSource.DevelopmentJwt, "owner", Canonical, key));
        CountingRepository repository = new();
        CountingUnitOfWork unitOfWork = new();
        ClaimReviewCaseHandler handler = CreateClaim(lookup, repository, unitOfWork, source: null, organizationId);

        Result<ChangeReviewCaseAssignmentResponse> result = await handler.Handle(
            new ClaimReviewCaseCommand(reviewCaseId, Token, Canonical, key),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ActorRequired, result.Error);
        Assert.Equal(0, lookup.Reads);
        Assert.Equal(0, repository.Reads);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Matching_history_returns_the_claim_after_the_case_was_released()
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseId reviewCaseId = ReviewCaseId.New();
        Guid key = Guid.NewGuid();
        ReviewCaseAssignmentEntry stored = Entry(
            organizationId,
            reviewCaseId,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            Canonical,
            key);
        CountingRepository repository = new(Open(organizationId, reviewCaseId));
        CountingUnitOfWork unitOfWork = new();
        ClaimReviewCaseHandler handler = CreateClaim(new ScriptedLookup(stored), repository, unitOfWork, ReviewCaseActorSource.DevelopmentJwt, organizationId);

        Result<ChangeReviewCaseAssignmentResponse> result = await handler.Handle(
            new ClaimReviewCaseCommand(reviewCaseId, Token, Canonical, key),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
        Assert.Equal("claim", result.Value.Assignment.Operation);
        Assert.Equal("owner", result.Value.Assignment.Assignee!.ActorId);
        Assert.Equal("developmentJwt", result.Value.Assignment.Assignee.ActorSource);
        Assert.Equal(0, repository.Reads);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Each_payload_component_conflicts_without_reading_the_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseId reviewCaseId = ReviewCaseId.New();
        Guid key = Guid.NewGuid();
        ReviewCaseAssignmentEntry stored = Entry(
            organizationId,
            reviewCaseId,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            Canonical,
            key);
        CountingRepository repository = new();
        ClaimReviewCaseCommand[] commands =
        [
            new(ReviewCaseId.New(), Token, Canonical, key),
            new(reviewCaseId, [8, 7, 6, 5, 4, 3, 2, 1], Convert.ToBase64String([8, 7, 6, 5, 4, 3, 2, 1]), key),
        ];
        foreach (ClaimReviewCaseCommand command in commands)
        {
            Result<ChangeReviewCaseAssignmentResponse> result = await CreateClaim(
                new ScriptedLookup(stored),
                repository,
                new CountingUnitOfWork(),
                ReviewCaseActorSource.DevelopmentJwt,
                organizationId).Handle(command, CancellationToken.None);
            Assert.Equal(ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict, result.Error);
        }

        Result<ChangeReviewCaseAssignmentResponse> otherSource = await CreateClaim(
            new ScriptedLookup(stored),
            repository,
            new CountingUnitOfWork(),
            ReviewCaseActorSource.ProductionCookie,
            organizationId).Handle(new ClaimReviewCaseCommand(reviewCaseId, Token, Canonical, key), CancellationToken.None);
        Result<ChangeReviewCaseAssignmentResponse> otherOperation = await CreateRelease(
            new ScriptedLookup(stored),
            repository,
            new CountingUnitOfWork(),
            ReviewCaseActorSource.DevelopmentJwt,
            organizationId).Handle(new ReleaseReviewCaseCommand(reviewCaseId, Token, Canonical, key), CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict, otherSource.Error);
        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict, otherOperation.Error);
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task A_mismatched_token_recovers_the_key_before_a_terminal_case_is_classified()
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseId reviewCaseId = ReviewCaseId.New();
        Guid key = Guid.NewGuid();
        ReviewCase closed = Open(organizationId, reviewCaseId);
        closed.Dismiss(null, Now);
        ReviewCaseAssignmentEntry stored = Entry(
            organizationId,
            reviewCaseId,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            Canonical,
            key);
        CountingRepository repository = new(closed, current: [9, 9, 9, 9, 9, 9, 9, 9]);
        ScriptedLookup lookup = new(null, stored);
        CountingUnitOfWork unitOfWork = new();
        ClaimReviewCaseHandler handler = CreateClaim(lookup, repository, unitOfWork, ReviewCaseActorSource.DevelopmentJwt, organizationId);

        Result<ChangeReviewCaseAssignmentResponse> result = await handler.Handle(
            new ClaimReviewCaseCommand(reviewCaseId, Token, Canonical, key),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
        Assert.Equal("claim", result.Value.Assignment.Operation);
        Assert.Equal(2, lookup.Reads);
        Assert.Equal(1, repository.Reads);
        Assert.Equal(0, unitOfWork.Saves);
        Assert.Equal(ReviewCaseStatus.Dismissed, closed.Status);
        Assert.Null(closed.AssignedActorId);
    }

    [Fact]
    public async Task A_mismatched_token_without_a_winner_is_a_concurrency_conflict()
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseId reviewCaseId = ReviewCaseId.New();
        CountingRepository repository = new(Open(organizationId, reviewCaseId), current: [9, 9, 9, 9, 9, 9, 9, 9]);
        CountingUnitOfWork unitOfWork = new();
        ClaimReviewCaseHandler handler = CreateClaim(new ScriptedLookup(null, null), repository, unitOfWork, ReviewCaseActorSource.DevelopmentJwt, organizationId);

        Result<ChangeReviewCaseAssignmentResponse> result = await handler.Handle(
            new ClaimReviewCaseCommand(reviewCaseId, Token, Canonical, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseAssignmentConcurrencyConflict, result.Error);
        Assert.Equal(0, unitOfWork.Saves);
        Assert.Null(repository.ReviewCase!.AssignedActorId);
    }

    private static ClaimReviewCaseHandler CreateClaim(
        ScriptedLookup lookup,
        CountingRepository repository,
        CountingUnitOfWork unitOfWork,
        ReviewCaseActorSource? source,
        OrganizationId organizationId)
    {
        return new ClaimReviewCaseHandler(
            new FixedOrganization(organizationId),
            new FixedAttribution(source, "owner"),
            repository,
            lookup,
            new RecordingWriter(),
            new NeverConflict(),
            new ClosedDirectory(),
            unitOfWork,
            new FixedTime());
    }

    private static ReleaseReviewCaseHandler CreateRelease(
        ScriptedLookup lookup,
        CountingRepository repository,
        CountingUnitOfWork unitOfWork,
        ReviewCaseActorSource? source,
        OrganizationId organizationId)
    {
        return new ReleaseReviewCaseHandler(
            new FixedOrganization(organizationId),
            new FixedAttribution(source, "owner"),
            repository,
            lookup,
            new RecordingWriter(),
            new NeverConflict(),
            new ClosedDirectory(),
            unitOfWork,
            new FixedTime());
    }

    private sealed class ClosedDirectory : IReviewCaseDelegationDirectory
    {
        public ReviewCaseDelegationDirectoryRead ReadCurrentOrganization()
        {
            return ReviewCaseDelegationDirectoryRead.Unavailable.Instance;
        }

        public bool Allows(OrganizationId organizationId, string normalizedRecipientActorId)
        {
            return false;
        }
    }

    private static ReviewCase Open(OrganizationId organizationId, ReviewCaseId reviewCaseId)
    {
        return ReviewCase.Open(
            reviewCaseId,
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            InvoiceId.New(),
            null,
            null,
            null,
            Opened);
    }

    private static ReviewCaseAssignmentEntry Entry(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        ReviewCaseAssignmentOperation operation,
        ReviewCaseActorSource source,
        string actorId,
        string token,
        Guid key)
    {
        return ReviewCaseAssignmentEntry.Create(
            ReviewCaseAssignmentEntryId.New(),
            organizationId,
            reviewCaseId,
            operation,
            source,
            actorId,
            token,
            key,
            Now,
            operation == ReviewCaseAssignmentOperation.Claim ? Now : null);
    }

    private sealed class FixedOrganization : ICurrentOrganizationContext
    {
        public FixedOrganization(OrganizationId organizationId)
        {
            OrganizationId = organizationId;
        }

        public OrganizationId OrganizationId { get; }
    }

    private sealed class FixedAttribution : ICurrentActorAttribution
    {
        private readonly ReviewCaseActorSource? _source;

        public FixedAttribution(ReviewCaseActorSource? source, string actorId)
        {
            _source = source;
            ActorId = actorId;
        }

        public string ActorId { get; }

        public bool TryGet(out ReviewCaseActorSource source, out string actorId)
        {
            source = _source ?? default;
            actorId = ActorId;
            return _source is not null;
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private sealed class ScriptedLookup : IReviewCaseAssignmentLookup
    {
        private readonly Queue<ReviewCaseAssignmentEntry?> _results;

        public ScriptedLookup(params ReviewCaseAssignmentEntry?[] results)
        {
            _results = new Queue<ReviewCaseAssignmentEntry?>(results);
        }

        public int Reads { get; private set; }

        public Task<ReviewCaseAssignmentEntry?> FindByIdempotencyKeyAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CountingRepository : IReviewCaseRepository
    {
        private readonly byte[] _current;

        public CountingRepository(ReviewCase? reviewCase = null, byte[]? current = null)
        {
            ReviewCase = reviewCase;
            _current = current ?? Token;
        }

        public ReviewCase? ReviewCase { get; }

        public int Reads { get; private set; }

        public void Add(ReviewCase reviewCase)
        {
        }

        public Task<ReviewCase?> GetByIdAsync(
            OrganizationId organizationId,
            ReviewCaseId reviewCaseId,
            CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(ReviewCase);
        }

        public byte[] GetRowVersion(ReviewCase reviewCase)
        {
            return _current.ToArray();
        }

        public void ExpectRowVersion(ReviewCase reviewCase, byte[] rowVersion)
        {
        }
    }

    private sealed class RecordingWriter : IReviewCaseAssignmentWriter
    {
        public void Add(ReviewCaseAssignmentEntry entry)
        {
        }
    }

    private sealed class NeverConflict : IReviewCaseAssignmentConflict
    {
        public bool IsIdempotencyKeyConflict(Exception exception)
        {
            return false;
        }
    }

    private sealed class CountingUnitOfWork : IUnitOfWork
    {
        public int Saves { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }
}