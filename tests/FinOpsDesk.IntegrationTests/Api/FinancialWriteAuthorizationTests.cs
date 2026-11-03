using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialWriteAuthorizationTests : IDisposable
{
    private const string OperatorName = "deploy-operator";

    private readonly SqlServerFixture _fixture;
    private readonly OidcProtocolStub _stub;

    public FinancialWriteAuthorizationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _stub = OidcProtocolStub.StartAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Both_activation_flags_are_required_and_jwt_is_unchanged_by_them()
    {
        await using Host closed = await Host.StartAsync(this, reads: false, writes: false);
        Assert.True(closed.State.ProductionFinancialAccessFailClosed);
        Assert.False(closed.State.ProductionFinancialWritesEnabled);
        await AssertConfirmStatusAsync(closed, HttpStatusCode.Forbidden);

        await using Host writeOnly = await Host.StartAsync(this, reads: false, writes: true);
        Assert.True(writeOnly.State.ProductionFinancialAccessFailClosed);
        Assert.False(writeOnly.State.ProductionFinancialWritesEnabled);
        await AssertConfirmStatusAsync(writeOnly, HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, (await writeOnly.Client.GetAsync("/api/invoices")).StatusCode);

        await using Host readOnly = await Host.StartAsync(this, reads: true, writes: false);
        Assert.False(readOnly.State.ProductionFinancialAccessFailClosed);
        Assert.False(readOnly.State.ProductionFinancialWritesEnabled);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.Client.GetAsync("/api/invoices")).StatusCode);
        await AssertConfirmStatusAsync(readOnly, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Write_matrix_follows_the_role_and_stays_inside_the_organization()
    {
        await using Host host = await Host.StartAsync(this, reads: true, writes: true);
        ApplicationUser user = await host.RequireUserAsync();
        Pair north = await host.SeedPairAsync("north");
        Pair south = await host.SeedPairAsync("south");
        Guid northMembership = await host.MembershipIdAsync(user.Id, north.OrganizationId);
        Guid southMembership = await host.MembershipIdAsync(user.Id, south.OrganizationId);
        await host.SelectAsync(north.OrganizationId);

        await AssertEveryWriteForbiddenAsync(host, north);
        Assert.Equal(0, await host.CountAllocationsAsync(north.OrganizationId));

        await host.GrantAsync(northMembership, OrganizationRole.OrganizationAdmin);
        await AssertEveryWriteForbiddenAsync(host, north);

        await host.GrantAsync(northMembership, OrganizationRole.Viewer);
        await AssertEveryWriteForbiddenAsync(host, north);

        await host.GrantAsync(northMembership, OrganizationRole.Operator);
        HttpResponseMessage confirmed = await host.SendAsync(Confirm(north));
        string confirmedBody = await confirmed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        Assert.Contains(user.Id.ToString("D"), await host.AuditActorsAsync(north.OrganizationId), StringComparer.Ordinal);
        using (JsonDocument document = JsonDocument.Parse(confirmedBody))
        {
            Assert.False(document.RootElement.GetProperty("replayed").GetBoolean());
        }

        HttpResponseMessage reverseDenied = await host.SendAsync(Reverse(north.AllocationId));
        Assert.Equal(HttpStatusCode.Forbidden, reverseDenied.StatusCode);
        HttpResponseMessage opened = await host.SendAsync(OpenReview(north));
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);

        await host.SelectAsync(south.OrganizationId);
        HttpResponseMessage foreign = await host.SendAsync(Confirm(north));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(1, await host.CountAllocationsAsync(north.OrganizationId));
        await host.SelectAsync(north.OrganizationId);
        await host.GrantAsync(southMembership, OrganizationRole.Operator);
        Pair southOnly = await host.SeedPairAsync("south-only", south.OrganizationId);
        using HttpRequestMessage spoofed = Confirm(southOnly, Guid.NewGuid(), host.Csrf);
        spoofed.Headers.TryAddWithoutValidation("X-Organization-Id", south.OrganizationId.ToString("D"));
        HttpResponseMessage spoofedResponse = await host.Client.SendAsync(spoofed);
        Assert.Equal(HttpStatusCode.NotFound, spoofedResponse.StatusCode);
        Assert.Equal(0, await host.CountAllocationsAsync(south.OrganizationId));

        await host.SelectAsync(south.OrganizationId);
        await host.RevokeAsync(southMembership, OrganizationRole.Operator);
        await host.GrantAsync(southMembership, OrganizationRole.ReversalApprover);
        HttpResponseMessage confirmDenied = await host.SendAsync(Confirm(south));
        Assert.Equal(HttpStatusCode.Forbidden, confirmDenied.StatusCode);
        Allocation southAllocation = await host.SeedAllocationAsync(south);
        HttpResponseMessage reversed = await host.SendAsync(Reverse(southAllocation.Id.Value, "customer returned the payment"));
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);

        await host.GrantAsync(southMembership, OrganizationRole.Operator);
        HttpResponseMessage imported = await host.Client.SendAsync(Import(host.Csrf));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
    }

    [Fact]
    public async Task Replay_without_the_write_permission_returns_403_and_does_not_mutate()
    {
        await using Host host = await Host.StartAsync(this, reads: true, writes: true);
        ApplicationUser user = await host.RequireUserAsync();
        Pair pair = await host.SeedPairAsync("replay");
        Guid membership = await host.MembershipIdAsync(user.Id, pair.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(pair.OrganizationId);
        Guid key = Guid.NewGuid();
        HttpResponseMessage created = await host.SendAsync(Confirm(pair, key));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        int before = await host.CountAllocationsAsync(pair.OrganizationId);

        await host.RevokeAsync(membership, OrganizationRole.Operator);
        HttpResponseMessage replay = await host.SendAsync(Confirm(pair, key));
        string body = await replay.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.DoesNotContain(pair.InvoiceId.ToString("D"), body, StringComparison.Ordinal);
        Assert.Equal(before, await host.CountAllocationsAsync(pair.OrganizationId));

        HttpResponseMessage conflict = await host.SendAsync(Confirm(pair, key));
        Assert.Equal(HttpStatusCode.Forbidden, conflict.StatusCode);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        Pair other = await host.SeedPairAsync("other-body", pair.OrganizationId);
        HttpResponseMessage mismatched = await host.SendAsync(Confirm(other, key));
        string mismatchBody = await mismatched.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, mismatched.StatusCode);
        Assert.Contains(ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code, mismatchBody, StringComparison.Ordinal);
        Assert.Equal(before, await host.CountAllocationsAsync(pair.OrganizationId));
    }

    [Fact]
    public async Task Shared_lock_commits_before_a_waiting_role_revocation()
    {
        PausingCommitSync sync = new();
        await using Host host = await Host.StartAsync(this, reads: true, writes: true, sync: sync);
        ApplicationUser user = await host.RequireUserAsync();
        Pair pair = await host.SeedPairAsync("shared-first");
        Guid membership = await host.MembershipIdAsync(user.Id, pair.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(pair.OrganizationId);

        Task<HttpResponseMessage> write = host.Client.SendAsync(Confirm(pair, Guid.NewGuid(), host.Csrf));
        try
        {
            await sync.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            Task revoke = host.RevokeAsync(membership, OrganizationRole.Operator);
            await WaitForApplicationWaiterAsync(pair.OrganizationId);
            Assert.False(revoke.IsCompleted);
            Assert.Equal(0, await host.CountAllocationsAsync(pair.OrganizationId));
            sync.Release();
            HttpResponseMessage response = await write;
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            await revoke;
            Assert.Equal(1, await host.CountAllocationsAsync(pair.OrganizationId));
        }
        finally
        {
            sync.Release();
        }
    }

    [Fact]
    public async Task Exclusive_lock_commits_the_revocation_before_the_write_is_denied()
    {
        await using Host host = await Host.StartAsync(this, reads: true, writes: true);
        ApplicationUser user = await host.RequireUserAsync();
        Pair pair = await host.SeedPairAsync("exclusive-first");
        Guid membership = await host.MembershipIdAsync(user.Id, pair.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(pair.OrganizationId);
        PausingRoleSync revokeSync = new();
        Task<OrganizationRoleMutationStatus> revoke = host.RevokePausedAsync(
            membership,
            OrganizationRole.Operator,
            revokeSync);
        try
        {
            await revokeSync.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            Task<HttpResponseMessage> write = host.Client.SendAsync(Confirm(pair, Guid.NewGuid(), host.Csrf));
            await WaitForApplicationWaiterAsync(pair.OrganizationId);
            Assert.Equal(0, await host.CountAllocationsAsync(pair.OrganizationId));
            revokeSync.Release();
            Assert.Equal(OrganizationRoleMutationStatus.Revoked, await revoke);
            HttpResponseMessage response = await write;
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, await host.CountAllocationsAsync(pair.OrganizationId));
        }
        finally
        {
            revokeSync.Release();
        }
    }

    [Fact]
    public async Task Lock_timeout_rolls_back_and_logout_waits_for_the_session_row()
    {
        PausingCommitSync sync = new();
        await using Host host = await Host.StartAsync(this, reads: true, writes: true, sync: sync, lockTimeoutMilliseconds: 400);
        ApplicationUser user = await host.RequireUserAsync();
        Pair pair = await host.SeedPairAsync("timeout");
        Guid membership = await host.MembershipIdAsync(user.Id, pair.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(pair.OrganizationId);
        await using SqlConnection held = new(_fixture.ConnectionString);
        await held.OpenAsync();
        await using SqlTransaction exclusive = (SqlTransaction)await held.BeginTransactionAsync();
        Assert.True(await AcquireAsync(held, exclusive, pair.OrganizationId, "Exclusive", 1000) >= 0);

        HttpResponseMessage timedOut = await host.SendAsync(Confirm(pair));
        string timedOutBody = await timedOut.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
        Assert.Contains("auth.authorization.lock_unavailable", timedOutBody, StringComparison.Ordinal);
        Assert.Equal(0, await host.CountAllocationsAsync(pair.OrganizationId));
        await exclusive.RollbackAsync();
        await held.DisposeAsync();

        Task<HttpResponseMessage> write = host.Client.SendAsync(Confirm(pair, Guid.NewGuid(), host.Csrf));
        try
        {
            await sync.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            Task<HttpResponseMessage> logout = host.Client.SendAsync(Logout(host.Csrf));
            await WaitForKeyWaiterAsync();
            Assert.False(logout.IsCompleted);
            sync.Release();
            Assert.Equal(HttpStatusCode.Created, (await write).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await logout).StatusCode);
            Assert.Equal(1, await host.CountAllocationsAsync(pair.OrganizationId));
        }
        finally
        {
            sync.Release();
        }
    }

    [Fact]
    public async Task Csrf_session_membership_and_invalid_role_fail_closed()
    {
        await using Host host = await Host.StartAsync(this, reads: true, writes: true);
        ApplicationUser user = await host.RequireUserAsync();
        Pair pair = await host.SeedPairAsync("closed");
        Guid membership = await host.MembershipIdAsync(user.Id, pair.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(pair.OrganizationId);

        using HttpRequestMessage missing = Confirm(pair, Guid.NewGuid(), csrf: null);
        HttpResponseMessage missingResponse = await host.Client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
        Assert.Contains("auth.csrf.invalid", await missingResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpRequestMessage invalid = Confirm(pair, Guid.NewGuid(), "not-a-token");
        HttpResponseMessage invalidResponse = await host.Client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        await using Host other = await Host.StartAsync(this, reads: true, writes: true);
        using HttpRequestMessage foreignCsrf = Confirm(pair, Guid.NewGuid(), other.Csrf);
        HttpResponseMessage foreignResponse = await host.Client.SendAsync(foreignCsrf);
        Assert.Equal(HttpStatusCode.BadRequest, foreignResponse.StatusCode);
        Assert.Equal(0, await host.CountAllocationsAsync(pair.OrganizationId));

        await host.RevokeMembershipAsync(membership);
        HttpResponseMessage membershipDenied = await host.SendAsync(Confirm(pair));
        Assert.Equal(HttpStatusCode.Forbidden, membershipDenied.StatusCode);
        await host.RestoreMembershipAsync(membership);

        await host.ExecuteAsync("ALTER TABLE access.OrganizationRoleAssignments NOCHECK CONSTRAINT CK_OrganizationRoleAssignments_Role");
        try
        {
            await host.InsertRoleAsync(membership, 9);
            HttpResponseMessage invalidRole = await host.SendAsync(Confirm(pair));
            string invalidBody = await invalidRole.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, invalidRole.StatusCode);
            Assert.Contains("auth.authorization.unavailable", invalidBody, StringComparison.Ordinal);
            Assert.DoesNotContain("SqlException", invalidBody, StringComparison.Ordinal);
        }
        finally
        {
            await host.ExecuteAsync("DELETE FROM access.OrganizationRoleAssignments WHERE Role = 9");
            await host.ExecuteAsync("ALTER TABLE access.OrganizationRoleAssignments WITH CHECK CHECK CONSTRAINT CK_OrganizationRoleAssignments_Role");
        }

        await host.ExecuteAsync("EXEC sp_rename N'access.OrganizationRoleAssignments', N'OrganizationRoleAssignments_offline';");
        try
        {
            HttpResponseMessage outage = await host.SendAsync(Confirm(pair));
            string outageBody = await outage.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
            Assert.Contains("auth.authorization.unavailable", outageBody, StringComparison.Ordinal);
            Assert.DoesNotContain("OrganizationRoleAssignments", outageBody, StringComparison.Ordinal);
        }
        finally
        {
            await host.ExecuteAsync("EXEC sp_rename N'access.OrganizationRoleAssignments_offline', N'OrganizationRoleAssignments';");
        }

        Assert.Equal(0, await host.CountAllocationsAsync(pair.OrganizationId));
        await host.DisableUserAsync(user.Id);
        HttpResponseMessage disabled = await host.SendAsync(Confirm(pair));
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
    }

    [Fact]
    public async Task Import_revoked_before_commit_inserts_no_rows_and_batch_stops_truthfully()
    {
        RevokeOnSecondItem sync = new();
        await using Host host = await Host.StartAsync(this, reads: true, writes: true, itemSync: sync);
        ApplicationUser user = await host.RequireUserAsync();
        Pair first = await host.SeedPairAsync("batch-a");
        Pair second = await host.SeedPairAsync("batch-b", first.OrganizationId);
        Guid membership = await host.MembershipIdAsync(user.Id, first.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(first.OrganizationId);
        sync.Attach(host, membership);
        HttpResponseMessage batch = await host.Client.SendAsync(Batch(first, second, host.Csrf));
        Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode);
        Assert.Equal(1, await host.CountAllocationsAsync(first.OrganizationId));
        Assert.Equal(0, await host.CountCompletedBatchesAsync(first.OrganizationId));

        await host.GrantAsync(membership, OrganizationRole.Operator);
        PausingRoleSync revokeSync = new();
        Task<OrganizationRoleMutationStatus> revoke = host.RevokePausedAsync(membership, OrganizationRole.Operator, revokeSync);
        try
        {
            await revokeSync.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            Task<HttpResponseMessage> import = host.Client.SendAsync(Import(host.Csrf));
            await WaitForApplicationWaiterAsync(first.OrganizationId);
            revokeSync.Release();
            Assert.Equal(OrganizationRoleMutationStatus.Revoked, await revoke);
            HttpResponseMessage imported = await import;
            Assert.Equal(HttpStatusCode.Forbidden, imported.StatusCode);
            Assert.Equal(0, await host.CountImportBatchesAsync(first.OrganizationId));
        }
        finally
        {
            revokeSync.Release();
        }
    }

    [Fact]
    public async Task Session_that_expires_while_waiting_for_the_lock_is_rejected()
    {
        ManualTimeProvider clock = new(TimeProvider.System.GetUtcNow());
        await using Host host = await Host.StartAsync(this, reads: true, writes: true, clock: clock);
        ApplicationUser user = await host.RequireUserAsync();
        Pair pair = await host.SeedPairAsync("expiry");
        Guid membership = await host.MembershipIdAsync(user.Id, pair.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(pair.OrganizationId);
        await using SqlConnection held = new(_fixture.ConnectionString);
        await held.OpenAsync();
        await using SqlTransaction exclusive = (SqlTransaction)await held.BeginTransactionAsync();
        Assert.True(await AcquireAsync(held, exclusive, pair.OrganizationId, "Exclusive", 1000) >= 0);
        try
        {
            Task<HttpResponseMessage> write = host.Client.SendAsync(Confirm(pair, Guid.NewGuid(), host.Csrf));
            await WaitForApplicationWaiterAsync(pair.OrganizationId);
            clock.Advance(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1));
            await exclusive.RollbackAsync();
            HttpResponseMessage response = await write;
            string body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("auth.commit.unauthenticated", body, StringComparison.Ordinal);
        }
        finally
        {
            if (exclusive.Connection is not null)
            {
                await exclusive.RollbackAsync();
            }
        }

        Assert.Equal(0, await host.CountAllocationsAsync(pair.OrganizationId));
        Assert.Equal(0, await host.CountAuditEntriesAsync(pair.OrganizationId));
        await AssertOrganizationLockReleasedAsync();
    }

    [Fact]
    public async Task Session_that_remains_valid_commits_after_waiting_for_the_lock()
    {
        await using Host host = await Host.StartAsync(this, reads: true, writes: true);
        ApplicationUser user = await host.RequireUserAsync();
        Pair pair = await host.SeedPairAsync("still-valid");
        Guid membership = await host.MembershipIdAsync(user.Id, pair.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(pair.OrganizationId);
        await using SqlConnection held = new(_fixture.ConnectionString);
        await held.OpenAsync();
        await using SqlTransaction exclusive = (SqlTransaction)await held.BeginTransactionAsync();
        Assert.True(await AcquireAsync(held, exclusive, pair.OrganizationId, "Exclusive", 1000) >= 0);
        try
        {
            Task<HttpResponseMessage> write = host.Client.SendAsync(Confirm(pair, Guid.NewGuid(), host.Csrf));
            await WaitForApplicationWaiterAsync(pair.OrganizationId);
            Assert.Equal(0, await host.CountAllocationsAsync(pair.OrganizationId));
            await exclusive.RollbackAsync();
            HttpResponseMessage response = await write;
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        finally
        {
            if (exclusive.Connection is not null)
            {
                await exclusive.RollbackAsync();
            }
        }

        Assert.Equal(1, await host.CountAllocationsAsync(pair.OrganizationId));
        Assert.Equal(1, await host.CountAuditEntriesAsync(pair.OrganizationId));
        await AssertOrganizationLockReleasedAsync();
    }

    [Fact]
    public async Task Running_batch_resumes_from_the_original_key_without_repeating_a_committed_item()
    {
        RevokeOnSecondItem sync = new();
        await using Host host = await Host.StartAsync(this, reads: true, writes: true, itemSync: sync);
        ApplicationUser user = await host.RequireUserAsync();
        Pair first = await host.SeedPairAsync("resume-a");
        Pair second = await host.SeedPairAsync("resume-b", first.OrganizationId);
        Guid membership = await host.MembershipIdAsync(user.Id, first.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(first.OrganizationId);
        sync.Attach(host, membership);
        Guid key = Guid.NewGuid();

        HttpResponseMessage interrupted = await host.Client.SendAsync(Batch(first, second, host.Csrf, key));
        Assert.Equal(HttpStatusCode.Forbidden, interrupted.StatusCode);
        Assert.Equal(1, await host.CountAllocationsAsync(first.OrganizationId));
        Assert.Equal(1, await host.CountRunningBatchesAsync(first.OrganizationId));
        Assert.Equal(0, await host.CountCompletedBatchesAsync(first.OrganizationId));

        HttpResponseMessage stillDenied = await host.Client.SendAsync(Batch(first, second, host.Csrf, key));
        Assert.Equal(HttpStatusCode.Forbidden, stillDenied.StatusCode);
        Assert.Equal(1, await host.CountAllocationsAsync(first.OrganizationId));
        Assert.Equal(1, await host.CountRunningBatchesAsync(first.OrganizationId));

        await host.GrantAsync(membership, OrganizationRole.Operator);
        HttpResponseMessage resumed = await host.Client.SendAsync(Batch(first, second, host.Csrf, key));
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(2, await host.CountAllocationsAsync(first.OrganizationId));
        Assert.Equal(0, await host.CountRunningBatchesAsync(first.OrganizationId));
        Assert.Equal(1, await host.CountCompletedBatchesAsync(first.OrganizationId));

        HttpResponseMessage replayed = await host.Client.SendAsync(Batch(first, second, host.Csrf, key));
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(2, await host.CountAllocationsAsync(first.OrganizationId));
        Assert.Equal(1, await host.CountCompletedBatchesAsync(first.OrganizationId));
    }

    [Fact]
    public async Task Permitted_cookie_writes_commit_allocation_import_review_and_batch_effects()
    {
        await using Host host = await Host.StartAsync(this, reads: true, writes: true);
        ApplicationUser user = await host.RequireUserAsync();
        Pair confirmPair = await host.SeedPairAsync("cover-confirm");
        Pair allocationPair = await host.SeedPairAsync("cover-allocation", confirmPair.OrganizationId);
        Pair reviewPair = await host.SeedPairAsync("cover-review", confirmPair.OrganizationId);
        Pair dismissPair = await host.SeedPairAsync("cover-dismiss", confirmPair.OrganizationId);
        Pair batchPair = await host.SeedPairAsync("cover-batch", confirmPair.OrganizationId);
        Pair batchOther = await host.SeedPairAsync("cover-batch-b", confirmPair.OrganizationId);
        Guid membership = await host.MembershipIdAsync(user.Id, confirmPair.OrganizationId);
        await host.GrantAsync(membership, OrganizationRole.Operator);
        await host.SelectAsync(confirmPair.OrganizationId);
        Guid confirmKey = Guid.NewGuid();

        HttpResponseMessage confirmed = await host.SendAsync(Confirm(confirmPair, confirmKey));
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        HttpResponseMessage replayed = await host.SendAsync(Confirm(confirmPair, confirmKey));
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync()))
        {
            Assert.True(document.RootElement.GetProperty("replayed").GetBoolean());
        }

        Assert.Equal(1, await host.CountAllocationsAsync(confirmPair.OrganizationId));
        HttpResponseMessage allocated = await host.SendAsync(Allocate(allocationPair, host.Csrf));
        Assert.Equal(HttpStatusCode.Created, allocated.StatusCode);
        Assert.Equal(2, await host.CountAllocationsAsync(confirmPair.OrganizationId));

        int invoicesBefore = await host.CountInvoicesAsync(confirmPair.OrganizationId);
        int banksBefore = await host.CountBankTransactionsAsync(confirmPair.OrganizationId);
        HttpResponseMessage imported = await host.Client.SendAsync(Import(host.Csrf));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        HttpResponseMessage importedAgain = await host.Client.SendAsync(Import(host.Csrf));
        Assert.Equal(HttpStatusCode.OK, importedAgain.StatusCode);
        Assert.Equal(invoicesBefore + 1, await host.CountInvoicesAsync(confirmPair.OrganizationId));
        HttpResponseMessage bankImported = await host.Client.SendAsync(BankImport(host.Csrf));
        Assert.Equal(HttpStatusCode.Created, bankImported.StatusCode);
        Assert.Equal(banksBefore + 1, await host.CountBankTransactionsAsync(confirmPair.OrganizationId));

        HttpResponseMessage opened = await host.SendAsync(OpenReview(reviewPair));
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        using JsonDocument openedDocument = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
        Guid reviewId = openedDocument.RootElement.GetProperty("reviewCaseId").GetGuid();
        string reviewToken = openedDocument.RootElement.GetProperty("concurrencyToken").GetString()!;
        HttpResponseMessage resolved = await host.SendAsync(ResolveReview(reviewId, reviewToken, host.Csrf));
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(1, await host.CountReviewCasesAsync(confirmPair.OrganizationId, ReviewCaseStatus.Resolved));

        HttpResponseMessage dismissOpened = await host.SendAsync(OpenReview(dismissPair));
        Assert.Equal(HttpStatusCode.Created, dismissOpened.StatusCode);
        using JsonDocument dismissDocument = JsonDocument.Parse(await dismissOpened.Content.ReadAsStringAsync());
        Guid dismissId = dismissDocument.RootElement.GetProperty("reviewCaseId").GetGuid();
        string dismissToken = dismissDocument.RootElement.GetProperty("concurrencyToken").GetString()!;
        HttpResponseMessage dismissed = await host.SendAsync(DismissReview(dismissId, dismissToken, host.Csrf));
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        Assert.Equal(1, await host.CountReviewCasesAsync(confirmPair.OrganizationId, ReviewCaseStatus.Dismissed));

        HttpResponseMessage batch = await host.Client.SendAsync(Batch(batchPair, batchOther, host.Csrf));
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        Assert.Equal(4, await host.CountAllocationsAsync(confirmPair.OrganizationId));
        Assert.Equal(1, await host.CountCompletedBatchesAsync(confirmPair.OrganizationId));
    }

    public void Dispose()
    {
        _stub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task WaitForApplicationWaiterAsync(Guid organizationId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            await using SqlConnection connection = new(_fixture.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using SqlCommand command = new(
                """
                SELECT COUNT(*)
                FROM sys.dm_tran_locks
                WHERE resource_type = N'APPLICATION' AND request_status = N'WAIT'
                """,
                connection);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException(
            "No waiting application lock was observed for " + organizationId.ToString("D") + ". " + await ReadLockSnapshotAsync());
    }

    private async Task WaitForKeyWaiterAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            await using SqlConnection connection = new(_fixture.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using SqlCommand command = new(
                """
                SELECT COUNT(*)
                FROM sys.dm_tran_locks
                WHERE resource_type = N'KEY' AND request_status = N'WAIT'
                """,
                connection);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException("No waiting row lock was observed. " + await ReadLockSnapshotAsync());
    }

    private async Task AssertOrganizationLockReleasedAsync()
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            """
            SELECT
                (SELECT COUNT(*)
                 FROM sys.dm_tran_locks
                 WHERE resource_database_id = DB_ID()
                   AND resource_type = N'APPLICATION')
                +
                (SELECT COUNT(*)
                 FROM sys.dm_exec_sessions
                 WHERE database_id = DB_ID()
                   AND session_id <> @@SPID
                   AND open_transaction_count > 0)
            """,
            connection);
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan interval)
        {
            _utcNow += interval;
        }
    }

    private async Task<string> ReadLockSnapshotAsync()
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            """
            SELECT resource_type, request_mode, request_status, resource_description
            FROM sys.dm_tran_locks
            WHERE resource_database_id = DB_ID()
              AND request_session_id <> @@SPID
            """,
            connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        StringBuilder snapshot = new();
        while (await reader.ReadAsync())
        {
            snapshot.Append(reader.GetString(0)).Append(' ').Append(reader.GetString(1)).Append(' ').Append(reader.GetString(2)).Append(' ').Append(reader.IsDBNull(3) ? "" : reader.GetString(3)).Append("; ");
        }

        return snapshot.Length == 0 ? "no locks" : snapshot.ToString();
    }

    private static async Task<int> AcquireAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid organizationId,
        string mode,
        int timeout)
    {
        await using SqlCommand command = new(
            """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = @mode,
                @LockOwner = 'Transaction',
                @LockTimeout = @timeout,
                @DbPrincipal = N'public';
            SELECT @result;
            """,
            connection,
            transaction);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255)
        {
            Value = "finopsdesk-auth:" + organizationId.ToString("D"),
        });
        command.Parameters.Add(new SqlParameter("@mode", SqlDbType.NVarChar, 32) { Value = mode });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = timeout });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task AssertConfirmStatusAsync(Host host, HttpStatusCode status)
    {
        HttpResponseMessage response = await host.SendAsync(Confirm(new Pair(Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty)));
        Assert.Equal(status, response.StatusCode);
    }

    private static async Task AssertEveryWriteForbiddenAsync(Host host, Pair pair)
    {
        string allocationId = Guid.NewGuid().ToString("D");
        string reviewId = Guid.NewGuid().ToString("D");
        HttpRequestMessage[] requests =
        [
            Import(host.Csrf),
            BankImport(host.Csrf),
            Confirm(pair, Guid.NewGuid(), host.Csrf),
            Batch(pair, pair, host.Csrf),
            Allocate(pair, host.Csrf),
            Reverse(Guid.Parse(allocationId), "not authorized", Guid.NewGuid(), host.Csrf),
            OpenReview(pair, host.Csrf),
            ReviewAction("/api/review-cases/" + reviewId + "/resolve", host.Csrf),
            ReviewAction("/api/review-cases/" + reviewId + "/dismiss", host.Csrf),
        ];
        foreach (HttpRequestMessage request in requests)
        {
            HttpResponseMessage response = await host.Client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, request.RequestUri + " " + response.StatusCode + " " + body);
            response.Dispose();
            request.Dispose();
        }
    }

    private static HttpRequestMessage Confirm(Pair pair)
    {
        return Confirm(pair, Guid.NewGuid(), null);
    }

    private static HttpRequestMessage Confirm(Pair pair, Guid key)
    {
        return Confirm(pair, key, null);
    }

    private static HttpRequestMessage Confirm(Pair pair, Guid key, string? csrf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/matches/confirm")
        {
            Content = new StringContent(
                "{\"invoiceId\":\"" + pair.InvoiceId.ToString("D") + "\",\"bankTransactionId\":\"" + pair.BankTransactionId.ToString("D") + "\"}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        if (csrf is not null)
        {
            request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        }

        return request;
    }

    private static HttpRequestMessage Allocate(Pair pair, string csrf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/allocations")
        {
            Content = new StringContent(
                "{\"invoiceId\":\"" + pair.InvoiceId.ToString("D")
                    + "\",\"bankTransactionId\":\"" + pair.BankTransactionId.ToString("D")
                    + "\",\"amount\":{\"amount\":10.00,\"currency\":\"PLN\"}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return request;
    }

    private static HttpRequestMessage Reverse(Guid allocationId)
    {
        return Reverse(allocationId, "customer returned the payment", Guid.NewGuid(), null);
    }

    private static HttpRequestMessage Reverse(Guid allocationId, string reason)
    {
        return Reverse(allocationId, reason, Guid.NewGuid(), null);
    }

    private static HttpRequestMessage Reverse(Guid allocationId, string reason, Guid key, string? csrf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/allocations/" + allocationId.ToString("D") + "/reverse")
        {
            Content = new StringContent("{\"reason\":" + JsonSerializer.Serialize(reason) + "}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        if (csrf is not null)
        {
            request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        }

        return request;
    }

    private static HttpRequestMessage OpenReview(Pair pair)
    {
        return OpenReview(pair, null);
    }

    private static HttpRequestMessage OpenReview(Pair pair, string? csrf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/review-cases")
        {
            Content = new StringContent(
                "{\"reason\":\"NeedsInvestigation\",\"invoiceId\":\"" + pair.InvoiceId.ToString("D") + "\"}",
                Encoding.UTF8,
                "application/json"),
        };
        if (csrf is not null)
        {
            request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        }

        return request;
    }

    private static HttpRequestMessage ReviewAction(string path, string csrf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent("{\"outcome\":\"Corrected\",\"resolutionNote\":\"done\",\"concurrencyToken\":\"AAAA\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return request;
    }

    private static HttpRequestMessage ResolveReview(Guid reviewCaseId, string token, string csrf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/review-cases/" + reviewCaseId.ToString("D") + "/resolve")
        {
            Content = new StringContent(
                "{\"outcome\":\"NoActionRequired\",\"resolutionNote\":\"nothing to do\",\"concurrencyToken\":" + JsonSerializer.Serialize(token) + "}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return request;
    }

    private static HttpRequestMessage DismissReview(Guid reviewCaseId, string token, string csrf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/review-cases/" + reviewCaseId.ToString("D") + "/dismiss")
        {
            Content = new StringContent(
                "{\"resolutionNote\":\"closed\",\"concurrencyToken\":" + JsonSerializer.Serialize(token) + "}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return request;
    }

    private static HttpRequestMessage Batch(Pair first, Pair second, string csrf)
    {
        return Batch(first, second, csrf, Guid.NewGuid());
    }

    private static HttpRequestMessage Batch(Pair first, Pair second, string csrf, Guid key)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/matches/batch/execute")
        {
            Content = new StringContent(
                "{\"pairs\":[{\"invoiceId\":\"" + first.InvoiceId.ToString("D") + "\",\"bankTransactionId\":\"" + first.BankTransactionId.ToString("D")
                    + "\"},{\"invoiceId\":\"" + second.InvoiceId.ToString("D") + "\",\"bankTransactionId\":\"" + second.BankTransactionId.ToString("D") + "\"}]}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return request;
    }

    private static HttpRequestMessage Import(string csrf)
    {
        return Form("/api/imports/invoices", "ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV/1,10.00,PLN\n", csrf);
    }

    private static HttpRequestMessage BankImport(string csrf)
    {
        return Form(
            "/api/imports/bank-transactions",
            "ExternalId,BookingDate,Amount,Currency,Reference\nTXN-1,2026-08-24,1.00,PLN,\n",
            csrf);
    }

    private static HttpRequestMessage Form(string path, string csv, string csrf)
    {
        MultipartFormDataContent content = new();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "import.csv");
        content.Add(new StringContent("mBank"), "sourceSystem");
        HttpRequestMessage request = new(HttpMethod.Post, path) { Content = content };
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return request;
    }

    private static HttpRequestMessage Logout(string csrf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/logout");
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return request;
    }

    private sealed class RevokeOnSecondItem : IBatchExactMatchItemSynchronization
    {
        private Host? _host;
        private Guid _membershipId;
        private int _calls;

        public void Attach(Host host, Guid membershipId)
        {
            _host = host;
            _membershipId = membershipId;
        }

        public Task BeforeItemAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                return _host!.RevokeAsync(_membershipId, OrganizationRole.Operator);
            }

            return Task.CompletedTask;
        }

        public Task OnItemReadyToCommitAsync(Guid itemKey, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class PausingCommitSync : IFinancialCommitLockSync
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Entered => _entered.Task;

        public void Release()
        {
            _release.TrySetResult();
        }

        public async Task AfterSharedAuthorizationAsync(CancellationToken cancellationToken)
        {
            // The idempotency lookup takes the lock briefly before the mutation.
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return;
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class PausingRoleSync : IOrganizationRoleMutationSync
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task BeforeAuthoritativeLockAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task AfterAuthoritativeLockAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed record Pair(Guid OrganizationId, Guid InvoiceId, Guid BankTransactionId, Guid AllocationId);

    private sealed class Host : IAsyncDisposable
    {
        private readonly FinancialWriteAuthorizationTests _test;
        private readonly FinOpsDeskApiFactory _factory;
        public HttpClient Client { get; }
        public string Csrf { get; private set; } = string.Empty;
        public AuthenticationCompositionState State { get; }

        private Host(FinancialWriteAuthorizationTests test, FinOpsDeskApiFactory factory, HttpClient client)
        {
            _test = test;
            _factory = factory;
            Client = client;
            State = factory.Services.GetRequiredService<AuthenticationCompositionState>();
        }

        public static async Task<Host> StartAsync(
            FinancialWriteAuthorizationTests test,
            bool reads,
            bool writes,
            IFinancialCommitLockSync? sync = null,
            IBatchExactMatchItemSynchronization? itemSync = null,
            int? lockTimeoutMilliseconds = null,
            TimeProvider? clock = null)
        {
            FinOpsDeskApiFactory factory = new(
                test._fixture.ConnectionString,
                services =>
                {
                    if (sync is not null)
                    {
                        services.RemoveAll<IFinancialCommitLockSync>();
                        services.AddSingleton(sync);
                    }

                    if (itemSync is not null)
                    {
                        services.RemoveAll<IBatchExactMatchItemSynchronization>();
                        services.AddSingleton(itemSync);
                    }

                    if (lockTimeoutMilliseconds is int timeout)
                    {
                        services.RemoveAll<SqlApplicationLockPolicy>();
                        services.AddSingleton(new SqlApplicationLockPolicy { WaitTimeoutMilliseconds = timeout });
                    }

                    if (clock is not null)
                    {
                        services.RemoveAll<TimeProvider>();
                        services.AddSingleton(clock);
                    }
                },
                environmentName: "Production",
                authenticationComposition: AuthenticationCompositions.ProductionCookie,
                configurationOverrides: new Dictionary<string, string?>
                {
                    ["Authentication:Oidc:Authority"] = test._stub.Authority,
                    ["Authentication:Oidc:ClientId"] = OidcProtocolStub.ClientId,
                    ["Authentication:Oidc:ClientSecret"] = OidcProtocolStub.ClientSecret,
                    ["Authentication:Oidc:BrowserOrigin"] = "https://localhost",
                    ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
                },
                financialReadAuthorizationEnabled: reads,
                financialWriteAuthorizationEnabled: writes);
            using HttpClient warmup = factory.CreateClient();
            Host host = new(test, factory, factory.CreateBrowserClient());
            await host.SignInAsync();
            if (reads)
            {
                ApplicationUser user = await host.RequireUserAsync();
                Guid organizationId = await host.CreateOrganizationAsync();
                await host.GrantMembershipAsync(user, organizationId);
                await host.GrantAsync(await host.MembershipIdAsync(user.Id, organizationId), OrganizationRole.Viewer);
                await host.SelectAsync(organizationId);
            }

            return host;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            if (!request.Headers.Contains(FinOpsDeskCsrf.HeaderName) && Csrf.Length > 0 && request.Method != HttpMethod.Get)
            {
                request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, Csrf);
            }

            return Client.SendAsync(request);
        }

        public async Task SelectAsync(Guid organizationId)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/session/organization")
            {
                Content = new StringContent("{\"organizationId\":\"" + organizationId.ToString("D") + "\"}", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, Csrf);
            HttpResponseMessage response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Csrf = await ReadCsrfAsync();
        }

        public async Task<Pair> SeedPairAsync(string name, Guid? organizationId = null)
        {
            Guid tenantId = organizationId ?? await CreateOrganizationAsync();
            if (organizationId is null)
            {
                ApplicationUser user = await RequireUserAsync();
                await GrantMembershipAsync(user, tenantId);
            }

            OrganizationId tenant = new(tenantId);
            string reference = name;
            Invoice invoice = new(InvoiceId.New(), tenant, reference, new Money(25m, PersistenceTestData.Pln));
            BankTransaction bank = new(
                BankTransactionId.New(),
                tenant,
                new Money(25m, PersistenceTestData.Pln),
                new DateOnly(2026, 11, 2),
                reference);
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            context.Invoices.Add(invoice);
            context.BankTransactions.Add(bank);
            await context.SaveChangesAsync();
            return new Pair(tenantId, invoice.Id.Value, bank.Id.Value, Guid.Empty);
        }

        public async Task<Allocation> SeedAllocationAsync(Pair pair)
        {
            OrganizationId tenant = new(pair.OrganizationId);
            Invoice? invoice = null;
            BankTransaction? bank = null;
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == new InvoiceId(pair.InvoiceId));
            bank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == new BankTransactionId(pair.BankTransactionId));
            Money amount = new(25m, PersistenceTestData.Pln);
            Assert.True(AllocationConfirmation.Apply(invoice, bank, amount).IsSuccess);
            Allocation allocation = new(
                AllocationId.New(),
                tenant,
                invoice.Id,
                bank.Id,
                amount,
                DateTimeOffset.UtcNow,
                Guid.NewGuid(),
                AllocationIdempotencyOperation.ExplicitAmount);
            context.Allocations.Add(allocation);
            await context.SaveChangesAsync();
            return allocation;
        }

        public async Task<int> CountAllocationsAsync(Guid organizationId)
        {
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            return await context.Allocations.CountAsync(allocation => allocation.OrganizationId == new OrganizationId(organizationId));
        }

        public async Task<int> CountImportBatchesAsync(Guid organizationId)
        {
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            return await context.ImportBatches.CountAsync(batch => batch.OrganizationId == new OrganizationId(organizationId));
        }

        public async Task<int> CountCompletedBatchesAsync(Guid organizationId)
        {
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            return await context.BatchExecutions.CountAsync(execution =>
                execution.OrganizationId == new OrganizationId(organizationId)
                && execution.Status == FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions.BatchExecutionStatus.Completed);
        }

        public async Task<int> CountRunningBatchesAsync(Guid organizationId)
        {
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            return await context.BatchExecutions.CountAsync(execution =>
                execution.OrganizationId == new OrganizationId(organizationId)
                && execution.Status == FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions.BatchExecutionStatus.Running);
        }

        public async Task<int> CountInvoicesAsync(Guid organizationId)
        {
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            return await context.Invoices.CountAsync(invoice => invoice.OrganizationId == new OrganizationId(organizationId));
        }

        public async Task<int> CountBankTransactionsAsync(Guid organizationId)
        {
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            return await context.BankTransactions.CountAsync(bank => bank.OrganizationId == new OrganizationId(organizationId));
        }

        public async Task<int> CountReviewCasesAsync(Guid organizationId, ReviewCaseStatus status)
        {
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            return await context.ReviewCases.CountAsync(reviewCase =>
                reviewCase.OrganizationId == new OrganizationId(organizationId)
                && reviewCase.Status == status);
        }

        public async Task<int> CountAuditEntriesAsync(Guid organizationId)
        {
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            return await context.FinancialAuditEntries.CountAsync(entry =>
                entry.OrganizationId == new OrganizationId(organizationId));
        }

        public async Task<IReadOnlyList<string>> AuditActorsAsync(Guid organizationId)
        {
            await using ReconciliationDbContext context = _test._fixture.CreateContext();
            return await context.FinancialAuditEntries
                .Where(entry => entry.OrganizationId == new OrganizationId(organizationId)
                    && entry.Action == FinancialAuditAction.Confirmation)
                .Select(entry => entry.ActorId)
                .ToListAsync();
        }

        public async Task<ApplicationUser> RequireUserAsync()
        {
            byte[] identityKey = ExternalIdentityKey.Compute(_test._stub.Authority, OidcProtocolStub.Subject);
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            ApplicationUser? user = (await context.ApplicationUsers.AsNoTracking()
                .Where(candidate => candidate.IdentityKey == identityKey)
                .ToListAsync())
                .SingleOrDefault(candidate => ExternalIdentityKey.Same(
                    candidate.Issuer,
                    candidate.Subject,
                    _test._stub.Authority,
                    OidcProtocolStub.Subject));
            Assert.NotNull(user);
            return user;
        }

        public async Task GrantAsync(Guid membershipId, OrganizationRole role)
        {
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            Assert.Equal(
                OrganizationRoleMutationStatus.Granted,
                await new OrganizationRoleStore(context).GrantAsync(
                    membershipId,
                    role,
                    DateTimeOffset.UtcNow,
                    RoleActor.DeploymentBootstrap(OperatorName),
                    CancellationToken.None));
        }

        public async Task RevokeAsync(Guid membershipId, OrganizationRole role)
        {
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            Assert.Equal(
                OrganizationRoleMutationStatus.Revoked,
                await new OrganizationRoleStore(context).RevokeAsync(
                    membershipId,
                    role,
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    RoleActor.DeploymentBootstrap(OperatorName),
                    CancellationToken.None));
        }

        public async Task<OrganizationRoleMutationStatus> RevokePausedAsync(
            Guid membershipId,
            OrganizationRole role,
            PausingRoleSync sync)
        {
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            return await new OrganizationRoleStore(context, sync).RevokeAsync(
                membershipId,
                role,
                DateTimeOffset.UtcNow.AddMinutes(1),
                RoleActor.DeploymentBootstrap(OperatorName),
                CancellationToken.None);
        }

        public async Task DisableUserAsync(Guid userId)
        {
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            ApplicationUser user = await context.ApplicationUsers.SingleAsync(candidate => candidate.Id == userId);
            user.Disable();
            await context.SaveChangesAsync();
        }

        public async Task RevokeMembershipAsync(Guid membershipId)
        {
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            OrganizationMembership membership = await context.OrganizationMemberships.SingleAsync(candidate => candidate.Id == membershipId);
            membership.Revoke(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }

        public async Task RestoreMembershipAsync(Guid membershipId)
        {
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            OrganizationMembership membership = await context.OrganizationMemberships.SingleAsync(candidate => candidate.Id == membershipId);
            membership.Restore();
            await context.SaveChangesAsync();
        }

        public async Task InsertRoleAsync(Guid membershipId, byte role)
        {
            await using SqlConnection connection = new(_test._fixture.ConnectionString);
            await connection.OpenAsync();
            await using SqlCommand command = new(
                """
                INSERT INTO access.OrganizationRoleAssignments
                    (Id, MembershipId, Role, GrantedAt, GrantActorKind, GrantActorUserId, GrantOperatorName,
                     RevokedAt, RevokeActorKind, RevokeActorUserId, RevokeOperatorName)
                VALUES
                    (@id, @membershipId, @role, SYSDATETIMEOFFSET(), 2, NULL, N'deploy-operator',
                     NULL, NULL, NULL, NULL)
                """,
                connection);
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
            command.Parameters.Add(new SqlParameter("@membershipId", SqlDbType.UniqueIdentifier) { Value = membershipId });
            command.Parameters.Add(new SqlParameter("@role", SqlDbType.TinyInt) { Value = role });
            await command.ExecuteNonQueryAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using SqlConnection connection = new(_test._fixture.ConnectionString);
            await connection.OpenAsync();
            await using SqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<Guid> MembershipIdAsync(Guid userId, Guid organizationId)
        {
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            return await context.OrganizationMemberships
                .Where(membership => membership.UserId == userId && membership.OrganizationId == organizationId)
                .Select(membership => membership.Id)
                .SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            _factory.Dispose();
            await Task.CompletedTask;
        }

        private async Task SignInAsync()
        {
            Csrf = await ReadCsrfAsync();
            using HttpRequestMessage login = new(HttpMethod.Post, "/login?returnUrl=/");
            login.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, Csrf);
            HttpResponseMessage loginResponse = await Client.SendAsync(login);
            Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
            using HttpClient provider = new(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage authorize = await provider.GetAsync(loginResponse.Headers.Location);
            Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
            HttpResponseMessage callback = await Client.GetAsync(authorize.Headers.Location!.PathAndQuery);
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            callback.Dispose();
            Csrf = await ReadCsrfAsync();
        }

        private async Task<string> ReadCsrfAsync()
        {
            HttpResponseMessage response = await Client.GetAsync("/api/session");
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("csrfToken").GetString()
                ?? throw new InvalidOperationException("CSRF token was missing.");
        }

        private async Task<Guid> CreateOrganizationAsync()
        {
            Guid organizationId = Guid.NewGuid();
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            DeploymentRoleBootstrap bootstrap = new(context, new OrganizationRoleStore(context), TimeProvider.System);
            Assert.Equal(
                DeploymentCommandStatus.OrganizationCreated,
                await bootstrap.CreateOrganizationAsync(organizationId, "Ledger " + organizationId.ToString("N"), CancellationToken.None));
            return organizationId;
        }

        private async Task GrantMembershipAsync(ApplicationUser user, Guid organizationId)
        {
            await using AccessDbContext context = _test._fixture.CreateAccessContext();
            DeploymentRoleBootstrap bootstrap = new(context, new OrganizationRoleStore(context), TimeProvider.System);
            Assert.Equal(
                DeploymentCommandStatus.MembershipGranted,
                await bootstrap.GrantMembershipAsync(user.Issuer, user.Subject, organizationId, CancellationToken.None));
        }
    }
}