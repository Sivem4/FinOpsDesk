using System.Security.Claims;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinOpsDesk.IntegrationTests.Api;

public sealed class AccessPermissionResolutionConcurrencyTests
{
    [Fact]
    public async Task Concurrent_callers_share_one_role_read_and_a_cancelled_waiter_does_not_cancel_it()
    {
        (StubHttpContextAccessor accessor, CancellationTokenSource requestAbort) = CreateRequest();
        using CancellationTokenSource waiterCancelled = new();
        ProbeRoleReader roles = new();
        Guid userId = Guid.NewGuid();
        Guid stamp = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        AccessSessionGate gate = CreateGate(roles, accessor, userId, stamp, organizationId);
        await ActivateAsync(gate, stamp, sessionId, organizationId);

        // The barrier inside the gate runs before the shared task is stored, so every caller
        // has entered ResolvePermissionsAsync while that task is still unpublished.
        CallerWave wave = new(gate, callerCount: 4, index => index == 0
            ? waiterCancelled.Token
            : CancellationToken.None);
        try
        {
            await roles.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(roles.Calls == 1, "Role reader invocations: " + roles.Calls);
            Assert.True(roles.MaxActive == 1, "Role reader concurrency: " + roles.MaxActive);
            Assert.True(roles.ObservedToken.Equals(requestAbort.Token));
            waiterCancelled.Cancel();
            Assert.False(roles.ObservedToken.IsCancellationRequested);
            roles.Release();
        }
        finally
        {
            roles.Release();
            wave.Join();
        }

        Assert.True(roles.Calls == 1, "An unobserved duplicate task would read roles again.");
        Assert.Equal(1, roles.MaxActive);
        Assert.IsAssignableFrom<OperationCanceledException>(wave.Outcomes[0].Error);
        Assert.Null(wave.Outcomes[0].Resolution);
        AccessPermissionResolution shared = wave.Outcomes[1].Resolution!;
        AssertViewer(shared);
        for (int index = 1; index < wave.Outcomes.Length; index++)
        {
            Assert.Null(wave.Outcomes[index].Error);
            Assert.Same(shared, wave.Outcomes[index].Resolution);
        }

        AccessSessionGate secondRequest = CreateGate(roles, accessor, userId, stamp, organizationId);
        await ActivateAsync(secondRequest, stamp, sessionId, organizationId);
        CallerWave secondWave = new(secondRequest, callerCount: 4, _ => CancellationToken.None);
        secondWave.Join();
        Assert.True(roles.Calls == 2, "Role reader invocations: " + roles.Calls);
        Assert.Equal(1, roles.MaxActive);
        AccessPermissionResolution again = secondWave.Outcomes[0].Resolution!;
        AssertViewer(again);
        Assert.NotSame(shared, again);
        for (int index = 1; index < secondWave.Outcomes.Length; index++)
        {
            Assert.Same(again, secondWave.Outcomes[index].Resolution);
        }

        requestAbort.Dispose();
    }

    [Fact]
    public async Task Unavailable_result_is_shared_and_is_not_an_empty_grant()
    {
        await AssertSharedFailureAsync(
            new InvalidOperationException("database offline"),
            "database");
    }

    [Fact]
    public async Task Inconsistent_role_is_shared_as_unavailable()
    {
        await AssertSharedFailureAsync(
            new InvalidOperationException("Organization role assignment is inconsistent."),
            "inconsistent-role");
    }

    [Fact]
    public async Task Empty_role_read_stays_resolved_and_distinct_from_unavailable()
    {
        (StubHttpContextAccessor accessor, CancellationTokenSource requestAbort) = CreateRequest();
        ProbeRoleReader roles = new(roles: []);
        Guid userId = Guid.NewGuid();
        Guid stamp = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        AccessSessionGate gate = CreateGate(roles, accessor, userId, stamp, organizationId);
        await ActivateAsync(gate, stamp, sessionId, organizationId);
        CallerWave wave = new(gate, callerCount: 4, _ => CancellationToken.None);
        try
        {
            await roles.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, roles.Calls);
            Assert.Equal(1, roles.MaxActive);
            roles.Release();
        }
        finally
        {
            roles.Release();
            wave.Join();
        }

        AccessPermissionResolution shared = wave.Outcomes[0].Resolution!;
        Assert.Equal(AccessPermissionResolutionKind.Resolved, shared.Kind);
        Assert.Equal("resolved", shared.Reason);
        Assert.Empty(shared.Permissions);
        Assert.NotEqual(AccessPermissionResolutionKind.Unavailable, shared.Kind);
        for (int index = 1; index < wave.Outcomes.Length; index++)
        {
            Assert.Same(shared, wave.Outcomes[index].Resolution);
        }

        ProbeRoleReader untouched = new();
        AccessSessionGate unselected = CreateGate(untouched, accessor, userId, stamp, organizationId);
        await ActivateAsync(unselected, stamp, sessionId, organizationId: null);
        AccessPermissionResolution empty = await unselected.ResolvePermissionsAsync(CancellationToken.None);
        Assert.Equal(AccessPermissionResolutionKind.Resolved, empty.Kind);
        Assert.Equal("no-active-organization", empty.Reason);
        Assert.Empty(empty.Permissions);
        Assert.Equal(0, untouched.Calls);
        requestAbort.Dispose();
    }

    [Fact]
    public async Task Inactive_session_does_not_start_the_role_reader()
    {
        (StubHttpContextAccessor accessor, CancellationTokenSource requestAbort) = CreateRequest();
        ProbeRoleReader roles = new();
        Guid userId = Guid.NewGuid();
        Guid stamp = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        AccessSessionGate gate = CreateGate(roles, accessor, userId, stamp, organizationId);
        gate.BeforePublishPermissionResolution = () => throw new InvalidOperationException("probe");
        AccessPermissionResolution unresolved = await gate.ResolvePermissionsAsync(CancellationToken.None);
        Assert.Equal(AccessPermissionResolutionKind.Untrusted, unresolved.Kind);
        Assert.Equal("session-not-resolved", unresolved.Reason);
        Assert.Empty(unresolved.Permissions);
        Assert.Equal(0, roles.Calls);
        gate.BeforePublishPermissionResolution = null;

        AccessSessionGate rejectedGate = CreateGate(
            roles,
            accessor,
            userId,
            stamp,
            organizationId,
            new MissingMembershipReader());
        ClaimsPrincipal principal = CookieSessionPrincipal.Create(
            "https://issuer.example",
            "subject",
            null,
            stamp,
            Guid.NewGuid(),
            organizationId);
        AccessSessionSnapshot snapshot = await rejectedGate.ResolveAsync(principal, CancellationToken.None);
        Assert.Equal(AccessSessionKind.Rejected, snapshot.Kind);

        CallerWave wave = new(rejectedGate, callerCount: 4, _ => CancellationToken.None);
        wave.Join();
        Assert.Equal(0, roles.Calls);
        Assert.Equal(0, roles.MaxActive);
        AccessPermissionResolution shared = wave.Outcomes[0].Resolution!;
        Assert.Equal(AccessPermissionResolutionKind.Untrusted, shared.Kind);
        Assert.Equal("missing-user", shared.Reason);
        Assert.Empty(shared.Permissions);
        for (int index = 1; index < wave.Outcomes.Length; index++)
        {
            Assert.Same(shared, wave.Outcomes[index].Resolution);
        }

        requestAbort.Dispose();
    }

    [Fact]
    public async Task Request_abort_cancels_the_shared_role_read()
    {
        (StubHttpContextAccessor accessor, CancellationTokenSource requestAbort) = CreateRequest();
        ProbeRoleReader roles = new();
        Guid userId = Guid.NewGuid();
        Guid stamp = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        AccessSessionGate gate = CreateGate(roles, accessor, userId, stamp, organizationId);
        await ActivateAsync(gate, stamp, Guid.NewGuid(), organizationId);

        Task<AccessPermissionResolution> pending = gate.ResolvePermissionsAsync(CancellationToken.None);
        try
        {
            await roles.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, roles.Calls);
            Assert.True(roles.ObservedToken.CanBeCanceled);
            Assert.True(roles.ObservedToken.Equals(requestAbort.Token));
            requestAbort.Cancel();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Exception? caught = await Record.ExceptionAsync(() => pending.WaitAsync(timeout.Token));
            Assert.IsAssignableFrom<OperationCanceledException>(caught);
            Assert.False(timeout.IsCancellationRequested);
            Assert.Equal(1, roles.Calls);
            Assert.Equal(1, roles.MaxActive);
        }
        finally
        {
            requestAbort.Cancel();
            roles.Release();
        }
    }

    private static async Task AssertSharedFailureAsync(Exception failure, string reason)
    {
        (StubHttpContextAccessor accessor, CancellationTokenSource requestAbort) = CreateRequest();
        ProbeRoleReader roles = new(failure: failure);
        Guid userId = Guid.NewGuid();
        Guid stamp = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        AccessSessionGate gate = CreateGate(roles, accessor, userId, stamp, organizationId);
        await ActivateAsync(gate, stamp, Guid.NewGuid(), organizationId);
        CallerWave wave = new(gate, callerCount: 4, _ => CancellationToken.None);
        try
        {
            await roles.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, roles.Calls);
            Assert.Equal(1, roles.MaxActive);
            roles.Release();
        }
        finally
        {
            roles.Release();
            wave.Join();
        }

        AccessPermissionResolution shared = wave.Outcomes[0].Resolution!;
        Assert.Equal(AccessPermissionResolutionKind.Unavailable, shared.Kind);
        Assert.Equal(reason, shared.Reason);
        Assert.Empty(shared.Permissions);
        Assert.NotEqual(AccessPermissionResolutionKind.Resolved, shared.Kind);
        for (int index = 1; index < wave.Outcomes.Length; index++)
        {
            Assert.Null(wave.Outcomes[index].Error);
            Assert.Same(shared, wave.Outcomes[index].Resolution);
        }

        Assert.Equal(1, roles.Calls);
        requestAbort.Dispose();
    }

    private static void AssertViewer(AccessPermissionResolution resolution)
    {
        Assert.Equal(AccessPermissionResolutionKind.Resolved, resolution.Kind);
        Assert.Equal("resolved", resolution.Reason);
        IReadOnlySet<AccessPermission> expected = OrganizationRolePermissions.For(OrganizationRole.Viewer);
        Assert.Equal(expected.Count, resolution.Permissions.Count);
        foreach (AccessPermission permission in expected)
        {
            Assert.Contains(permission, resolution.Permissions);
        }

        Assert.DoesNotContain(AccessPermission.AuditRead, resolution.Permissions);
    }

    private static async Task ActivateAsync(
        AccessSessionGate gate,
        Guid stamp,
        Guid sessionId,
        Guid? organizationId)
    {
        ClaimsPrincipal principal = CookieSessionPrincipal.Create(
            "https://issuer.example",
            "subject",
            null,
            stamp,
            sessionId,
            organizationId);
        AccessSessionSnapshot snapshot = await gate.ResolveAsync(principal, CancellationToken.None);
        Assert.Equal(AccessSessionKind.Active, snapshot.Kind);
        if (organizationId is Guid organization)
        {
            Assert.Equal(organization, snapshot.ActiveOrganization!.OrganizationId);
        }
        else
        {
            Assert.Null(snapshot.ActiveOrganization);
        }
    }

    private static AccessSessionGate CreateGate(
        IOrganizationRoleReader roles,
        IHttpContextAccessor httpContextAccessor,
        Guid userId,
        Guid stamp,
        Guid organizationId,
        IOrganizationMembershipReader? memberships = null)
    {
        return new AccessSessionGate(
            memberships ?? new FixedMembershipReader(userId, stamp, organizationId),
            new FixedBrowserSessionStore(userId),
            roles,
            TimeProvider.System,
            NullLogger<AccessSessionGate>.Instance,
            httpContextAccessor);
    }

    private static (StubHttpContextAccessor Accessor, CancellationTokenSource RequestAbort) CreateRequest()
    {
        CancellationTokenSource requestAbort = new();
        DefaultHttpContext httpContext = new()
        {
            RequestAborted = requestAbort.Token,
        };
        return (new StubHttpContextAccessor(httpContext), requestAbort);
    }

    private sealed class CallerWave
    {
        private readonly Barrier _barrier;
        private readonly AccessSessionGate _gate;
        private bool _joined;

        public CallerWave(AccessSessionGate gate, int callerCount, Func<int, CancellationToken> tokenFor)
        {
            _gate = gate;
            _barrier = new Barrier(callerCount);
            Outcomes = new CallerOutcome[callerCount];
            Threads = new Thread[callerCount];
            gate.BeforePublishPermissionResolution = () => _barrier.SignalAndWait();
            for (int index = 0; index < callerCount; index++)
            {
                int caller = index;
                CancellationToken token = tokenFor(caller);
                Threads[index] = new Thread(() => Complete(caller, token))
                {
                    IsBackground = true,
                };
                Threads[index].Start();
            }
        }

        public Thread[] Threads { get; }

        public CallerOutcome[] Outcomes { get; }

        public void Join()
        {
            if (_joined)
            {
                return;
            }

            _joined = true;
            foreach (Thread thread in Threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Permission caller did not finish.");
            }

            _gate.BeforePublishPermissionResolution = null;
            _barrier.Dispose();
        }

        private void Complete(int caller, CancellationToken token)
        {
            try
            {
                Outcomes[caller] = new CallerOutcome
                {
                    Resolution = _gate.ResolvePermissionsAsync(token).GetAwaiter().GetResult(),
                };
            }
            catch (Exception exception)
            {
                Outcomes[caller] = new CallerOutcome
                {
                    Error = exception,
                };
            }
        }
    }

    private sealed class CallerOutcome
    {
        public AccessPermissionResolution? Resolution { get; init; }

        public Exception? Error { get; init; }
    }

    private sealed class ProbeRoleReader : IOrganizationRoleReader
    {
        private readonly OrganizationRole[] _roles;
        private readonly Exception? _failure;
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _active;
        private int _maxActive;
        private CancellationToken _observedToken;

        public ProbeRoleReader(OrganizationRole[]? roles = null, Exception? failure = null)
        {
            _roles = roles ?? [OrganizationRole.Viewer];
            _failure = failure;
        }

        public int Calls => Volatile.Read(ref _calls);

        public int MaxActive => Volatile.Read(ref _maxActive);

        public Task Entered => _entered.Task;

        public CancellationToken ObservedToken => _observedToken;

        public void Release()
        {
            _release.TrySetResult(true);
        }

        public async Task<IReadOnlyList<OrganizationRole>> ListActiveAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            int observedMax;
            do
            {
                observedMax = Volatile.Read(ref _maxActive);
                if (active <= observedMax)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref _maxActive, active, observedMax) != observedMax);

            int call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                _observedToken = cancellationToken;
                _entered.TrySetResult(true);
            }

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                if (_failure is not null)
                {
                    throw _failure;
                }

                return _roles;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class FixedMembershipReader(Guid userId, Guid sessionStamp, Guid organizationId) : IOrganizationMembershipReader
    {
        public Task<IReadOnlyList<OrganizationChoice>> ListActiveAsync(
            string issuer,
            string subject,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<MembershipResolution> ResolveAsync(
            string issuer,
            string subject,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(MembershipResolution.Active(
                userId,
                sessionStamp,
                [new OrganizationChoice(organizationId, "North Ledger")]));
        }

        public Task RetireDisabledSessionAsync(
            Guid userId,
            Guid observedStamp,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class MissingMembershipReader : IOrganizationMembershipReader
    {
        public Task<IReadOnlyList<OrganizationChoice>> ListActiveAsync(
            string issuer,
            string subject,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<MembershipResolution> ResolveAsync(
            string issuer,
            string subject,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(MembershipResolution.Missing());
        }

        public Task RetireDisabledSessionAsync(
            Guid userId,
            Guid observedStamp,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedBrowserSessionStore(Guid userId) : IBrowserSessionStore
    {
        public Task<Guid> CreateAsync(
            Guid userId,
            DateTimeOffset createdAt,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<BrowserSessionState?> FindAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult<BrowserSessionState?>(new BrowserSessionState(
                sessionId,
                userId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1),
                null));
        }

        public Task<BrowserSessionRevocation> RevokeAsync(
            Guid sessionId,
            Guid userId,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubHttpContextAccessor(HttpContext? httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}