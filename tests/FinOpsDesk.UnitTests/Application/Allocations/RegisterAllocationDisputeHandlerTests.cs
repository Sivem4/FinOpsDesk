using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Application.Allocations;

public sealed class RegisterAllocationDisputeHandlerTests
{
    private static readonly OrganizationId Organization = OrganizationId.New();
    private static readonly AllocationId Allocation = AllocationId.New();
    private static readonly DateTimeOffset RecordedAt = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Missing_actor_returns_before_the_idempotency_key_is_read()
    {
        ScriptedLookup lookup = new(throwOnRead: true);
        RegisterAllocationDisputeHandler handler = CreateHandler(lookup, new ScriptedWriter(new ScriptedLock()), actor: false);

        Result<RegisterAllocationDisputeResult> result = await handler.Handle(
            new RegisterAllocationDisputeCommand(Allocation, "The customer disputes this payment.", Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ReconciliationApplicationErrors.ActorRequired.Code, result.Error.Code);
        Assert.Equal(0, lookup.Calls);
    }

    [Fact]
    public async Task Reversed_allocation_classifies_a_key_committed_after_the_first_miss()
    {
        AllocationDispute stored = Dispute("The customer disputes this payment.");
        ScriptedLock row = new() { Found = true, Status = AllocationStatus.Reversed };
        ScriptedLookup lookup = new(null, stored);
        lookup.OnCall = call =>
        {
            if (call == 2)
            {
                Assert.Equal(1, row.Rollbacks);
            }
        };
        RegisterAllocationDisputeHandler handler = CreateHandler(lookup, new ScriptedWriter(row), actor: true);

        Result<RegisterAllocationDisputeResult> result = await handler.Handle(
            new RegisterAllocationDisputeCommand(Allocation, stored.Statement, stored.IdempotencyKey),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
        Assert.Equal(stored.Id.Value, result.Value.Dispute.DisputeId);
        Assert.Equal(0, row.Commits);
    }

    [Fact]
    public async Task Missing_key_after_rollback_returns_not_found_or_not_confirmed()
    {
        ScriptedLock missing = new() { Found = false };
        ScriptedLookup lookup = new(null, null);
        lookup.OnCall = call =>
        {
            if (call == 2)
            {
                Assert.Equal(1, missing.Rollbacks);
            }
        };
        RegisterAllocationDisputeHandler handler = CreateHandler(lookup, new ScriptedWriter(missing), actor: true);

        Result<RegisterAllocationDisputeResult> result = await handler.Handle(
            new RegisterAllocationDisputeCommand(Allocation, "The customer disputes this payment.", Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ReconciliationApplicationErrors.AllocationNotFound.Code, result.Error.Code);
    }

    private static RegisterAllocationDisputeHandler CreateHandler(
        ScriptedLookup lookup,
        ScriptedWriter writer,
        bool actor)
    {
        return new RegisterAllocationDisputeHandler(
            new FixedOrganization(),
            new FixedActor(actor),
            lookup,
            writer,
            new AlwaysIdempotencyConflict(),
            new NoUnitOfWork(),
            new FixedTime());
    }

    private static AllocationDispute Dispute(string statement)
    {
        return AllocationDispute.Create(
            AllocationDisputeId.New(),
            Organization,
            Allocation,
            statement,
            ReviewCaseActorSource.DevelopmentJwt,
            "operator-a",
            RecordedAt,
            Guid.NewGuid());
    }

    private sealed class ScriptedLookup : IAllocationDisputeLookup
    {
        private readonly Queue<AllocationDispute?> _results;

        public ScriptedLookup(bool throwOnRead)
        {
            _results = new Queue<AllocationDispute?>();
            ThrowOnRead = throwOnRead;
        }

        public ScriptedLookup(params AllocationDispute?[] results)
        {
            _results = new Queue<AllocationDispute?>(results);
        }

        public int Calls { get; private set; }

        public bool ThrowOnRead { get; }

        public Action<int>? OnCall { get; set; }

        public Task<AllocationDispute?> FindByIdempotencyKeyAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            Calls++;
            OnCall?.Invoke(Calls);
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("The idempotency key was read.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ScriptedWriter : IAllocationDisputeWriter
    {
        private readonly ScriptedLock _lock;

        public ScriptedWriter(ScriptedLock row)
        {
            _lock = row;
        }

        public Task<IAllocationRowLock> LockAllocationAsync(
            OrganizationId organizationId,
            AllocationId allocationId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IAllocationRowLock>(_lock);
        }

        public void Add(AllocationDispute dispute)
        {
        }
    }

    private sealed class ScriptedLock : IAllocationRowLock
    {
        public bool Found { get; init; }

        public AllocationStatus? Status { get; init; }

        public int Rollbacks { get; private set; }

        public int Commits { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Commits++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            Rollbacks++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedOrganization : ICurrentOrganizationContext
    {
        public OrganizationId OrganizationId => Organization;
    }

    private sealed class FixedActor : ICurrentActorAttribution
    {
        private readonly bool _present;

        public FixedActor(bool present)
        {
            _present = present;
        }

        public bool TryGet(out ReviewCaseActorSource source, out string actorId)
        {
            source = ReviewCaseActorSource.DevelopmentJwt;
            actorId = "operator-a";
            return _present;
        }
    }

    private sealed class AlwaysIdempotencyConflict : IAllocationDisputeConflict
    {
        public bool IsIdempotencyKeyConflict(Exception exception)
        {
            return true;
        }
    }

    private sealed class NoUnitOfWork : IUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return RecordedAt;
        }
    }
}