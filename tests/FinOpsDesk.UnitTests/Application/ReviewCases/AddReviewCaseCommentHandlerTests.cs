using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.ReviewCases;

public sealed class AddReviewCaseCommentHandlerTests
{
    [Fact]
    public async Task Missing_actor_is_rejected_before_the_key_is_read()
    {
        ScriptedLookup lookup = new(new ReviewCaseComment?[] { null });
        RecordingWriter writer = new();
        CountingQueries queries = new(exists: true);
        CountingUnitOfWork unitOfWork = new();
        AddReviewCaseCommentHandler handler = CreateHandler(
            lookup,
            writer,
            queries,
            unitOfWork,
            new FixedActorContext(null));

        Result<AddReviewCaseCommentResponse> result = await handler.Handle(
            Command(ReviewCaseId.New(), "kept", Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ActorRequired, result.Error);
        Assert.Equal(0, lookup.Reads);
        Assert.Equal(0, queries.ExistsReads);
        Assert.Equal(0, writer.Added);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Matching_stored_key_returns_the_original_comment_without_a_save()
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseId reviewCaseId = ReviewCaseId.New();
        Guid key = Guid.NewGuid();
        ReviewCaseComment stored = Comment(organizationId, reviewCaseId, "kept", "original-actor", key, Earlier);
        ScriptedLookup lookup = new(stored);
        CountingUnitOfWork unitOfWork = new();
        AddReviewCaseCommentHandler handler = CreateHandler(
            lookup,
            new RecordingWriter(),
            new CountingQueries(exists: false),
            unitOfWork,
            new FixedActorContext("later-actor"),
            organizationId,
            Later);

        Result<AddReviewCaseCommentResponse> result = await handler.Handle(
            Command(reviewCaseId, "  kept  ", key),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
        Assert.Equal(stored.Id.Value, result.Value.Comment.Id);
        Assert.Equal("original-actor", result.Value.Comment.ActorId);
        Assert.Equal(Earlier, result.Value.Comment.RecordedAt);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Different_payload_conflicts_before_case_existence_is_checked()
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseComment stored = Comment(
            organizationId,
            ReviewCaseId.New(),
            "kept",
            "original-actor",
            Guid.NewGuid(),
            Earlier);
        CountingQueries queries = new(exists: false);
        CountingUnitOfWork unitOfWork = new();
        AddReviewCaseCommentHandler handler = CreateHandler(
            new ScriptedLookup(stored),
            new RecordingWriter(),
            queries,
            unitOfWork,
            new FixedActorContext("later-actor"),
            organizationId);

        Result<AddReviewCaseCommentResponse> result = await handler.Handle(
            Command(ReviewCaseId.New(), "kept", stored.IdempotencyKey),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyPayloadConflict, result.Error);
        Assert.Equal(0, queries.ExistsReads);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Named_conflict_without_a_winner_is_a_concurrency_conflict_and_is_not_saved_again()
    {
        ScriptedLookup lookup = new(new ReviewCaseComment?[] { null, null });
        RecordingWriter writer = new();
        CountingUnitOfWork unitOfWork = new(new UniqueConstraintConflictException("unique"));
        AddReviewCaseCommentHandler handler = CreateHandler(
            lookup,
            writer,
            new CountingQueries(exists: true),
            unitOfWork,
            new FixedActorContext("operator-a"),
            conflict: new AlwaysCommentConflict(isKeyConflict: true));

        Result<AddReviewCaseCommentResponse> result = await handler.Handle(
            Command(ReviewCaseId.New(), "kept", Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseCommentConcurrencyConflict, result.Error);
        Assert.Equal(1, writer.Added);
        Assert.Equal(1, unitOfWork.Saves);
        Assert.Equal(2, lookup.Reads);
    }

    [Fact]
    public async Task Named_conflict_with_a_different_winner_is_a_payload_conflict()
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseId reviewCaseId = ReviewCaseId.New();
        Guid key = Guid.NewGuid();
        ReviewCaseComment winner = Comment(organizationId, reviewCaseId, "other", "original-actor", key, Earlier);
        CountingUnitOfWork unitOfWork = new(new UniqueConstraintConflictException("unique"));
        AddReviewCaseCommentHandler handler = CreateHandler(
            new ScriptedLookup(new ReviewCaseComment?[] { null, winner }),
            new RecordingWriter(),
            new CountingQueries(exists: true),
            unitOfWork,
            new FixedActorContext("later-actor"),
            organizationId,
            conflict: new AlwaysCommentConflict(isKeyConflict: true));

        Result<AddReviewCaseCommentResponse> result = await handler.Handle(
            Command(reviewCaseId, "kept", key),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyPayloadConflict, result.Error);
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task A_unique_violation_that_is_not_the_named_index_is_not_replay()
    {
        CountingUnitOfWork unitOfWork = new(new UniqueConstraintConflictException("primary key"));
        AddReviewCaseCommentHandler handler = CreateHandler(
            new ScriptedLookup(new ReviewCaseComment?[] { null }),
            new RecordingWriter(),
            new CountingQueries(exists: true),
            unitOfWork,
            new FixedActorContext("operator-a"),
            conflict: new AlwaysCommentConflict(isKeyConflict: false));

        UniqueConstraintConflictException exception = await Assert.ThrowsAsync<UniqueConstraintConflictException>(() =>
            handler.Handle(Command(ReviewCaseId.New(), "kept", Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("primary key", exception.Message);
        Assert.Equal(1, unitOfWork.Saves);
    }

    private static readonly DateTimeOffset Earlier = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private static AddReviewCaseCommentCommand Command(ReviewCaseId reviewCaseId, string text, Guid key)
    {
        return new AddReviewCaseCommentCommand(reviewCaseId, text, key);
    }

    private static ReviewCaseComment Comment(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string text,
        string actorId,
        Guid key,
        DateTimeOffset recordedAt)
    {
        return ReviewCaseComment.Create(
            ReviewCaseCommentId.New(),
            organizationId,
            reviewCaseId,
            text,
            actorId,
            recordedAt,
            key);
    }

    private static AddReviewCaseCommentHandler CreateHandler(
        IReviewCaseCommentLookup lookup,
        IReviewCaseCommentWriter writer,
        IReviewCaseCommentQueries queries,
        IUnitOfWork unitOfWork,
        ICurrentActorContext actor,
        OrganizationId? organizationId = null,
        DateTimeOffset? utcNow = null,
        IReviewCaseCommentConflict? conflict = null)
    {
        return new AddReviewCaseCommentHandler(
            new RecordingOrganizationContext(organizationId ?? OrganizationId.New()),
            actor,
            lookup,
            writer,
            conflict ?? new AlwaysCommentConflict(isKeyConflict: false),
            queries,
            unitOfWork,
            new FixedTimeProvider(utcNow ?? Later));
    }

    private sealed class ScriptedLookup : IReviewCaseCommentLookup
    {
        private readonly Queue<ReviewCaseComment?> _results;

        public ScriptedLookup(params ReviewCaseComment?[] results)
        {
            _results = new Queue<ReviewCaseComment?>(results);
        }

        public int Reads { get; private set; }

        public Task<ReviewCaseComment?> FindByIdempotencyKeyAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(_results.Count == 0 ? null : _results.Dequeue());
        }
    }

    private sealed class RecordingWriter : IReviewCaseCommentWriter
    {
        public int Added { get; private set; }

        public void Add(ReviewCaseComment comment)
        {
            Added++;
        }
    }

    private sealed class CountingQueries : IReviewCaseCommentQueries
    {
        private readonly bool _exists;

        public CountingQueries(bool exists)
        {
            _exists = exists;
        }

        public int ExistsReads { get; private set; }

        public Task<bool> ExistsAsync(
            OrganizationId organizationId,
            ReviewCaseId reviewCaseId,
            CancellationToken cancellationToken)
        {
            ExistsReads++;
            return Task.FromResult(_exists);
        }

        public Task<Result<ReviewCaseCommentListResponse>> GetForReviewCaseAsync(
            OrganizationId organizationId,
            ReviewCaseId reviewCaseId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CountingUnitOfWork : IUnitOfWork
    {
        private readonly Exception? _exception;

        public CountingUnitOfWork(Exception? exception = null)
        {
            _exception = exception;
        }

        public int Saves { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysCommentConflict : IReviewCaseCommentConflict
    {
        private readonly bool _isKeyConflict;

        public AlwaysCommentConflict(bool isKeyConflict)
        {
            _isKeyConflict = isKeyConflict;
        }

        public bool IsIdempotencyKeyConflict(Exception exception)
        {
            return _isKeyConflict;
        }
    }
}