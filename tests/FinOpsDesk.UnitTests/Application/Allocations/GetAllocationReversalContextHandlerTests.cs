using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Allocations;

public sealed class GetAllocationReversalContextHandlerTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public async Task Handle_should_forward_the_current_organization_allocation_and_cancellation_token()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        AllocationId allocationId = AllocationId.New();
        AllocationReversalContextResponse response = Sample(allocationId);
        FakeAllocationReversalContextQueries queries = new(response);
        GetAllocationReversalContextHandler handler = new(organizationContext, queries);
        using CancellationTokenSource cancellationTokenSource = new();

        Result<AllocationReversalContextResponse> result = await handler.Handle(
            new GetAllocationReversalContextQuery(allocationId),
            cancellationTokenSource.Token);

        Assert.True(result.IsSuccess);
        Assert.Same(response, result.Value);
        Assert.Equal(1, organizationContext.ReadCount);
        Assert.Equal(TestOrganizationId, queries.OrganizationId);
        Assert.Equal(allocationId, queries.AllocationId);
        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task Handle_should_return_not_found_when_the_allocation_is_absent()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        FakeAllocationReversalContextQueries queries = new(response: null);
        GetAllocationReversalContextHandler handler = new(organizationContext, queries);

        Result<AllocationReversalContextResponse> result = await handler.Handle(
            new GetAllocationReversalContextQuery(AllocationId.New()),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ReconciliationApplicationErrors.AllocationNotFound, result.Error);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task Handle_should_not_query_when_the_organization_context_fails()
    {
        FakeAllocationReversalContextQueries queries = new(response: null);
        GetAllocationReversalContextHandler handler = new(new ThrowingOrganizationContext(), queries);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new GetAllocationReversalContextQuery(AllocationId.New()),
                CancellationToken.None));

        Assert.Equal(0, queries.CallCount);
    }

    private static AllocationReversalContextResponse Sample(AllocationId allocationId)
    {
        return new AllocationReversalContextResponse(
            allocationId.Value,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "INV-1",
            null,
            new MoneyDto(1m, "PLN"),
            "confirmed",
            DateTimeOffset.UnixEpoch,
            null,
            null);
    }

    private sealed class RecordingOrganizationContext : ICurrentOrganizationContext
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

    private sealed class ThrowingOrganizationContext : ICurrentOrganizationContext
    {
        public OrganizationId OrganizationId =>
            throw new InvalidOperationException(
                "A valid organization claim is required on the authenticated user.");
    }

    private sealed class FakeAllocationReversalContextQueries : IAllocationReversalContextQueries
    {
        private readonly AllocationReversalContextResponse? _response;

        public FakeAllocationReversalContextQueries(AllocationReversalContextResponse? response)
        {
            _response = response;
        }

        public int CallCount { get; private set; }

        public OrganizationId? OrganizationId { get; private set; }

        public AllocationId? AllocationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AllocationReversalContextResponse?> FindAsync(
            OrganizationId organizationId,
            AllocationId allocationId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OrganizationId = organizationId;
            AllocationId = allocationId;
            CancellationToken = cancellationToken;
            return Task.FromResult(_response);
        }
    }
}