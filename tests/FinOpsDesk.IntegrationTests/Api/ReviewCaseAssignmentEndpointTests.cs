using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCaseAssignmentEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ReviewCaseAssignmentEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Claim_and_release_change_only_the_assignment_and_a_later_close_freezes_it()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Invoice invoice = await SeedInvoiceAsync(organizationId, "assignment-claim");
        Guid reviewCaseId = await OpenAsync(client, invoice.Id.Value);
        string beforeToken = await DetailTokenAsync(client, reviewCaseId);
        decimal outstanding = await OutstandingAsync(invoice.Id);
        int audits = await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        int comments = await CountAsync(context => context.ReviewCaseComments.CountAsync(comment => comment.OrganizationId == organizationId));
        int allocations = await CountAsync(context => context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));

        HttpResponseMessage claimed = await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), beforeToken, Guid.NewGuid());
        string claimedBody = await claimed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, claimed.StatusCode);
        Assert.Null(claimed.Headers.Location);
        using JsonDocument claimDocument = JsonDocument.Parse(claimedBody);
        Assert.Equal(reviewCaseId, claimDocument.RootElement.GetProperty("reviewCaseId").GetGuid());
        Assert.Equal("claim", claimDocument.RootElement.GetProperty("operation").GetString());
        Assert.Equal("developmentJwt", claimDocument.RootElement.GetProperty("assignee").GetProperty("actorSource").GetString());
        Assert.Equal("test-user", claimDocument.RootElement.GetProperty("assignee").GetProperty("actorId").GetString());
        Assert.False(claimDocument.RootElement.TryGetProperty("concurrencyToken", out _));
        Assert.False(claimDocument.RootElement.TryGetProperty("organizationId", out _));

        HttpResponseMessage current = await client.GetAsync(AssignmentPath(reviewCaseId));
        string currentBody = await current.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using JsonDocument currentDocument = JsonDocument.Parse(currentBody);
        Assert.Equal("test-user", currentDocument.RootElement.GetProperty("assignee").GetProperty("actorId").GetString());
        string claimedToken = currentDocument.RootElement.GetProperty("concurrencyToken").GetString()!;
        Assert.NotEqual(beforeToken, claimedToken);

        HttpResponseMessage released = await PostAssignmentAsync(client, ReleasePath(reviewCaseId), claimedToken, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, released.StatusCode);
        using JsonDocument releaseDocument = JsonDocument.Parse(await released.Content.ReadAsStringAsync());
        Assert.Equal("release", releaseDocument.RootElement.GetProperty("operation").GetString());
        Assert.Equal(JsonValueKind.Null, releaseDocument.RootElement.GetProperty("assignee").ValueKind);
        Assert.False(releaseDocument.RootElement.TryGetProperty("concurrencyToken", out _));

        string releasedToken = await AssignmentTokenAsync(client, reviewCaseId);
        HttpResponseMessage claimedAgain = await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), releasedToken, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, claimedAgain.StatusCode);
        string frozenToken = await AssignmentTokenAsync(client, reviewCaseId);
        await ResolveAsync(client, reviewCaseId, frozenToken);
        HttpResponseMessage frozen = await client.GetAsync(AssignmentPath(reviewCaseId));
        using JsonDocument frozenDocument = JsonDocument.Parse(await frozen.Content.ReadAsStringAsync());
        Assert.Equal("test-user", frozenDocument.RootElement.GetProperty("assignee").GetProperty("actorId").GetString());
        await AssertProblemAsync(
            await PostAssignmentAsync(client, ReleasePath(reviewCaseId), frozenDocument.RootElement.GetProperty("concurrencyToken").GetString()!, Guid.NewGuid()),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentTerminal.Code);
        Assert.Equal(3, await HistoryCountAsync(organizationId));
        Assert.Equal(audits + 1, await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId)));
        Assert.Equal(comments, await CountAsync(context => context.ReviewCaseComments.CountAsync(comment => comment.OrganizationId == organizationId)));
        Assert.Equal(allocations, await CountAsync(context => context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId)));
        Assert.Equal(outstanding, await OutstandingAsync(invoice.Id));
        Assert.Equal(ReviewCaseStatus.Resolved, (await ReadCaseAsync(reviewCaseId)).Status);
        Assert.Equal("test-user", (await ReadCaseAsync(reviewCaseId)).ActorId);
    }

    [Fact]
    public async Task A_new_key_cannot_take_or_overwrite_an_assignment_and_a_foreign_release_hides_the_holder()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient owner = _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "owner"));
        using HttpClient other = _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "other"));
        Guid reviewCaseId = await OpenAsync(owner, (await SeedInvoiceAsync(organizationId, "assignment-held")).Id.Value);
        string token = await DetailTokenAsync(owner, reviewCaseId);
        Assert.Equal(HttpStatusCode.Created, (await PostAssignmentAsync(owner, AssignmentPath(reviewCaseId), token, Guid.NewGuid())).StatusCode);
        string heldToken = await AssignmentTokenAsync(owner, reviewCaseId);

        await AssertProblemAsync(
            await PostAssignmentAsync(owner, AssignmentPath(reviewCaseId), heldToken, Guid.NewGuid()),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentAlreadyAssigned.Code);
        await AssertProblemAsync(
            await PostAssignmentAsync(other, AssignmentPath(reviewCaseId), heldToken, Guid.NewGuid()),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentAlreadyAssigned.Code);
        HttpResponseMessage foreignRelease = await PostAssignmentAsync(other, ReleasePath(reviewCaseId), heldToken, Guid.NewGuid());
        string foreignBody = await foreignRelease.Content.ReadAsStringAsync();
        await AssertProblemAsync(foreignRelease, HttpStatusCode.Conflict, ReconciliationApplicationErrors.ReviewCaseAssignmentNotAssignee.Code);
        Assert.DoesNotContain("owner", foreignBody, StringComparison.Ordinal);
        Assert.DoesNotContain("developmentJwt", foreignBody, StringComparison.Ordinal);
        Assert.Equal("owner", (await ReadCaseAsync(reviewCaseId)).ActorId);
        Assert.Equal(1, await HistoryCountAsync(organizationId));
    }

    [Fact]
    public async Task The_same_actor_text_from_another_source_is_not_the_assignee()
    {
        OrganizationId organizationId = OrganizationId.New();
        using FinOpsDeskApiFactory cookieSource = new(
            _fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICurrentActorAttribution>();
                services.AddScoped<ICurrentActorAttribution>(_ => new FixedAttribution(ReviewCaseActorSource.ProductionCookie, "test-user"));
            });
        using HttpClient jwt = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using HttpClient cookie = cookieSource.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        Guid reviewCaseId = await OpenAsync(jwt, (await SeedInvoiceAsync(organizationId, "assignment-source")).Id.Value);
        string token = await DetailTokenAsync(jwt, reviewCaseId);
        Assert.Equal(HttpStatusCode.Created, (await PostAssignmentAsync(jwt, AssignmentPath(reviewCaseId), token, Guid.NewGuid())).StatusCode);

        await AssertProblemAsync(
            await PostAssignmentAsync(cookie, ReleasePath(reviewCaseId), await AssignmentTokenAsync(jwt, reviewCaseId), Guid.NewGuid()),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentNotAssignee.Code);
        Assert.Equal(ReviewCaseActorSource.DevelopmentJwt, (await ReadCaseAsync(reviewCaseId)).Source);
    }

    [Fact]
    public async Task Releasing_an_empty_assignment_conflicts_and_a_matching_replay_survives_later_changes()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "assignment-replay")).Id.Value);
        string openToken = await DetailTokenAsync(client, reviewCaseId);
        await AssertProblemAsync(
            await PostAssignmentAsync(client, ReleasePath(reviewCaseId), openToken, Guid.NewGuid()),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentNotAssigned.Code);

        Guid key = Guid.NewGuid();
        HttpResponseMessage created = await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), openToken, key);
        string createdBody = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string withSpace = openToken.Insert(4, " \r\n\t");
        HttpResponseMessage replay = await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), withSpace, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(createdBody, await replay.Content.ReadAsStringAsync());

        await PostAssignmentAsync(client, ReleasePath(reviewCaseId), await AssignmentTokenAsync(client, reviewCaseId), Guid.NewGuid());
        await DismissAsync(client, reviewCaseId);
        HttpResponseMessage afterClose = await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), withSpace, key);
        Assert.Equal(HttpStatusCode.OK, afterClose.StatusCode);
        Assert.Equal(createdBody, await afterClose.Content.ReadAsStringAsync());
        Assert.Equal(ReviewCaseStatus.Dismissed, (await ReadCaseAsync(reviewCaseId)).Status);
        Assert.Null((await ReadCaseAsync(reviewCaseId)).ActorId);
        Assert.Equal(2, await HistoryCountAsync(organizationId));
    }

    [Fact]
    public async Task A_reused_key_conflicts_for_every_payload_component()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient owner = _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "owner"));
        using HttpClient other = _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "other"));
        Guid left = await OpenAsync(owner, (await SeedInvoiceAsync(organizationId, "assignment-left")).Id.Value);
        Guid right = await OpenAsync(owner, (await SeedInvoiceAsync(organizationId, "assignment-right")).Id.Value);
        Guid key = Guid.NewGuid();
        string token = await DetailTokenAsync(owner, left);
        Assert.Equal(HttpStatusCode.Created, (await PostAssignmentAsync(owner, AssignmentPath(left), token, key)).StatusCode);
        string current = await AssignmentTokenAsync(owner, left);

        await AssertProblemAsync(
            await PostAssignmentAsync(other, AssignmentPath(left), current, key),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict.Code);
        await AssertProblemAsync(
            await PostAssignmentAsync(owner, ReleasePath(left), current, key),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict.Code);
        await AssertProblemAsync(
            await PostAssignmentAsync(owner, AssignmentPath(right), await DetailTokenAsync(owner, right), key),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict.Code);
        byte[] decoded = Convert.FromBase64String(token);
        decoded[0]++;
        await AssertProblemAsync(
            await PostAssignmentAsync(owner, AssignmentPath(left), Convert.ToBase64String(decoded), key),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict.Code);
        Assert.Equal(1, await HistoryCountAsync(organizationId));
        Assert.Equal("owner", (await ReadCaseAsync(left)).ActorId);
        Assert.Null((await ReadCaseAsync(right)).ActorId);
    }

    [Fact]
    public async Task A_missing_actor_is_rejected_for_a_new_command_and_a_replay()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient actor = Client(organizationId);
        using HttpClient missing = _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, null));
        Guid reviewCaseId = await OpenAsync(actor, (await SeedInvoiceAsync(organizationId, "assignment-actor")).Id.Value);
        Guid key = Guid.NewGuid();
        string token = await DetailTokenAsync(actor, reviewCaseId);

        await AssertProblemAsync(
            await PostAssignmentAsync(missing, AssignmentPath(reviewCaseId), token, Guid.NewGuid()),
            HttpStatusCode.Forbidden,
            ReconciliationApplicationErrors.ActorRequired.Code);
        Assert.Equal(HttpStatusCode.Created, (await PostAssignmentAsync(actor, AssignmentPath(reviewCaseId), token, key)).StatusCode);
        await AssertProblemAsync(
            await PostAssignmentAsync(missing, AssignmentPath(reviewCaseId), token, key),
            HttpStatusCode.Forbidden,
            ReconciliationApplicationErrors.ActorRequired.Code);
        Assert.Equal(1, await HistoryCountAsync(organizationId));
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_a_stored_key_and_existing_reads_omit_the_assignee()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "assignment-input")).Id.Value);
        string token = await DetailTokenAsync(client, reviewCaseId);
        Guid key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), token, key)).StatusCode);

        await AssertProblemAsync(
            await PostRawAsync(client, AssignmentPath(reviewCaseId), "{\"concurrencyToken\":\"" + token + "\",\"assigneeId\":\"other\"}", key.ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, AssignmentPath(reviewCaseId), "{\"concurrencyToken\":\"" + token + "\"}", " " + key.ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, AssignmentPath(reviewCaseId), "{\"concurrencyToken\":\"" + token + "\"}", null),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyKeyRequired.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, AssignmentPath(reviewCaseId), "{\"concurrencyToken\":\"not-a-token\"}", Guid.NewGuid().ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, AssignmentPath(reviewCaseId), "text", Guid.NewGuid().ToString("D"), "text/plain"),
            HttpStatusCode.UnsupportedMediaType,
            ReconciliationApplicationErrors.ReviewCaseUnsupportedMediaType.Code);
        await AssertProblemAsync(
            await client.GetAsync(AssignmentPath(reviewCaseId) + "?organizationId=" + organizationId.Value.ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest.Code);

        string detail = await (await client.GetAsync("/api/review-cases/" + reviewCaseId.ToString("D"))).Content.ReadAsStringAsync();
        string list = await (await client.GetAsync("/api/review-cases")).Content.ReadAsStringAsync();
        string summary = await (await client.GetAsync("/api/review-cases/summary")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("assignee", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("assignee", list, StringComparison.Ordinal);
        Assert.DoesNotContain("assignee", summary, StringComparison.Ordinal);
        Assert.Equal(1, await HistoryCountAsync(organizationId));
    }

    [Fact]
    public async Task Unknown_and_foreign_cases_share_not_found_and_organizations_reuse_a_key()
    {
        OrganizationId first = OrganizationId.New();
        OrganizationId second = OrganizationId.New();
        using HttpClient firstClient = _factory.CreateApiClient(TestAccessTokens.CreateForActor(first.Value, "actor-a"));
        using HttpClient secondClient = _factory.CreateApiClient(TestAccessTokens.CreateForActor(second.Value, "actor-b"));
        Guid firstCase = await OpenAsync(firstClient, (await SeedInvoiceAsync(first, "assignment-a")).Id.Value);
        Guid secondCase = await OpenAsync(secondClient, (await SeedInvoiceAsync(second, "assignment-b")).Id.Value);
        Guid key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostAssignmentAsync(firstClient, AssignmentPath(firstCase), await DetailTokenAsync(firstClient, firstCase), key)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAssignmentAsync(secondClient, AssignmentPath(secondCase), await DetailTokenAsync(secondClient, secondCase), key)).StatusCode);

        await AssertProblemAsync(
            await firstClient.GetAsync(AssignmentPath(Guid.NewGuid())),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code);
        await AssertProblemAsync(
            await secondClient.GetAsync(AssignmentPath(firstCase)),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code);
        Assert.Equal("actor-a", (await ReadCaseAsync(firstCase)).ActorId);
        Assert.Equal("actor-b", (await ReadCaseAsync(secondCase)).ActorId);
    }

    [Fact]
    public async Task A_matching_key_committed_before_the_loser_reads_the_case_replays()
    {
        AssignmentRaceProbe probe = new(AssignmentRaceMode.HoldSecondMissUntilSave);
        using FinOpsDeskApiFactory factory = RaceFactory(probe);
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient first = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-a"));
        using HttpClient second = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-a"));
        Guid reviewCaseId = await OpenAsync(first, (await SeedInvoiceAsync(organizationId, "assignment-replay-race")).Id.Value);
        string token = await DetailTokenAsync(first, reviewCaseId);
        Guid key = Guid.NewGuid();
        probe.Arm();

        Task<HttpResponseMessage> leader = PostAssignmentAsync(first, AssignmentPath(reviewCaseId), token, key);
        await probe.WaitForMissesAsync(1);
        Task<HttpResponseMessage> follower = PostAssignmentAsync(second, AssignmentPath(reviewCaseId), token, key);
        HttpResponseMessage[] responses = await Task.WhenAll(leader, follower);

        Assert.Equal(1, probe.Commits);
        Assert.Equal(0, probe.ConcurrencyFailures);
        Assert.Equal(0, probe.NamedConflicts);
        Assert.Equal(1, await HistoryCountAsync(organizationId));
        HttpResponseMessage created = responses.Single(response => response.StatusCode == HttpStatusCode.Created);
        HttpResponseMessage replayed = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(await created.Content.ReadAsStringAsync(), await replayed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Competing_keys_leave_one_assignment_and_a_concurrency_conflict()
    {
        AssignmentRaceProbe probe = new(AssignmentRaceMode.ReleaseAfterTwoCaseReads);
        using FinOpsDeskApiFactory factory = RaceFactory(probe);
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient first = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-a"));
        using HttpClient second = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-b"));
        Guid reviewCaseId = await OpenAsync(first, (await SeedInvoiceAsync(organizationId, "assignment-keys")).Id.Value);
        string token = await DetailTokenAsync(first, reviewCaseId);
        probe.Arm();

        HttpResponseMessage[] responses = await Task.WhenAll(
            PostAssignmentAsync(first, AssignmentPath(reviewCaseId), token, Guid.NewGuid()),
            PostAssignmentAsync(second, AssignmentPath(reviewCaseId), token, Guid.NewGuid()));

        Assert.Equal(1, probe.Commits);
        Assert.Equal(1, probe.ConcurrencyFailures);
        Assert.Equal(0, probe.NamedConflicts);
        Assert.Equal(1, await HistoryCountAsync(organizationId));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
        await AssertProblemAsync(
            responses.Single(response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentConcurrencyConflict.Code);
        Assert.Contains((await ReadCaseAsync(reviewCaseId)).ActorId, new[] { "actor-a", "actor-b" });
    }

    [Fact]
    public async Task A_shared_key_with_a_different_case_uses_the_named_idempotency_index()
    {
        AssignmentRaceProbe probe = new(AssignmentRaceMode.ReleaseAfterTwoMisses);
        using FinOpsDeskApiFactory factory = RaceFactory(probe);
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient first = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-a"));
        using HttpClient second = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-b"));
        Guid left = await OpenAsync(first, (await SeedInvoiceAsync(organizationId, "assignment-shared-a")).Id.Value);
        Guid right = await OpenAsync(first, (await SeedInvoiceAsync(organizationId, "assignment-shared-b")).Id.Value);
        Guid key = Guid.NewGuid();
        probe.Arm();

        HttpResponseMessage[] responses = await Task.WhenAll(
            PostAssignmentAsync(first, AssignmentPath(left), await DetailTokenAsync(first, left), key),
            PostAssignmentAsync(second, AssignmentPath(right), await DetailTokenAsync(first, right), key));

        Assert.Equal(1, probe.Commits);
        Assert.Equal(0, probe.ConcurrencyFailures);
        Assert.Equal(1, probe.NamedConflicts);
        Assert.Equal(1, await HistoryCountAsync(organizationId));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
        await AssertProblemAsync(
            responses.Single(response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict.Code);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Assignment_and_close_race_in_both_commit_orders(bool claim, bool assignmentWins)
    {
        AssignmentRaceProbe probe = new(AssignmentRaceMode.WinnerThenLoserReads);
        using FinOpsDeskApiFactory factory = RaceFactory(probe);
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "owner"));
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "assignment-close-" + claim + assignmentWins)).Id.Value);
        if (!claim)
        {
            Assert.Equal(
                HttpStatusCode.Created,
                (await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), await DetailTokenAsync(client, reviewCaseId), Guid.NewGuid())).StatusCode);
        }

        int historyBefore = await HistoryCountAsync(organizationId);
        int auditsBefore = await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        string token = await AssignmentTokenAsync(client, reviewCaseId);
        string assignmentPath = claim ? AssignmentPath(reviewCaseId) : ReleasePath(reviewCaseId);
        string closePath = claim
            ? "/api/review-cases/" + reviewCaseId.ToString("D") + "/resolve"
            : "/api/review-cases/" + reviewCaseId.ToString("D") + "/dismiss";
        probe.Arm();
        Task<HttpResponseMessage> winner = assignmentWins
            ? PostAssignmentAsync(client, assignmentPath, token, Guid.NewGuid())
            : PostCloseAsync(client, closePath, token, claim);
        await probe.WaitForCaseReadsAsync(1);
        Task<HttpResponseMessage> loser = assignmentWins
            ? PostCloseAsync(client, closePath, token, claim)
            : PostAssignmentAsync(client, assignmentPath, token, Guid.NewGuid());
        HttpResponseMessage[] responses = await Task.WhenAll(winner, loser);

        Assert.Equal(1, probe.SaveAttempts);
        Assert.Equal(1, probe.Commits);
        Assert.Equal(0, probe.ConcurrencyFailures);
        Assert.Equal(0, probe.NamedConflicts);
        Assert.Equal(assignmentWins ? HttpStatusCode.Created : HttpStatusCode.OK, responses[0].StatusCode);
        CaseRow row = await ReadCaseAsync(reviewCaseId);
        if (assignmentWins)
        {
            Assert.Equal(historyBefore + 1, await HistoryCountAsync(organizationId));
            Assert.Equal(auditsBefore, await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId)));
            Assert.Equal(ReviewCaseStatus.Open, row.Status);
            Assert.Equal(claim ? "owner" : null, row.ActorId);
            await AssertProblemAsync(
                responses.Single(response => response.StatusCode == HttpStatusCode.Conflict),
                HttpStatusCode.Conflict,
                ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code);
        }
        else
        {
            Assert.Equal(historyBefore, await HistoryCountAsync(organizationId));
            Assert.Equal(auditsBefore + 1, await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId)));
            Assert.Equal(claim ? ReviewCaseStatus.Resolved : ReviewCaseStatus.Dismissed, row.Status);
            Assert.Equal(claim ? null : "owner", row.ActorId);
            await AssertProblemAsync(
                responses.Single(response => response.StatusCode == HttpStatusCode.Conflict),
                HttpStatusCode.Conflict,
                ReconciliationApplicationErrors.ReviewCaseAssignmentConcurrencyConflict.Code);
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task Assignment_and_close_save_the_same_rowversion_in_both_commit_orders(
        bool claim,
        bool resolve,
        bool assignmentWins)
    {
        AssignmentRaceProbe probe = new(AssignmentRaceMode.OrderedSaveAfterSharedRead);
        using FinOpsDeskApiFactory factory = RaceFactory(probe);
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "owner"));
        Invoice invoice = await SeedInvoiceAsync(organizationId, "assignment-write-" + claim + resolve + assignmentWins);
        Guid reviewCaseId = await OpenAsync(client, invoice.Id.Value);
        if (!claim)
        {
            Assert.Equal(
                HttpStatusCode.Created,
                (await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), await DetailTokenAsync(client, reviewCaseId), Guid.NewGuid())).StatusCode);
        }

        int historyBefore = await HistoryCountAsync(organizationId);
        int auditsBefore = await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        int resolvedBefore = await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry =>
            entry.OrganizationId == organizationId && entry.Action == ReviewCaseAuditAction.Resolved));
        int dismissedBefore = await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry =>
            entry.OrganizationId == organizationId && entry.Action == ReviewCaseAuditAction.Dismissed));
        int commentsBefore = await CountAsync(context => context.ReviewCaseComments.CountAsync(comment => comment.OrganizationId == organizationId));
        int allocationsBefore = await CountAsync(context => context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        decimal outstanding = await OutstandingAsync(invoice.Id);
        string token = await AssignmentTokenAsync(client, reviewCaseId);
        string assignmentPath = claim ? AssignmentPath(reviewCaseId) : ReleasePath(reviewCaseId);
        string closePath = "/api/review-cases/" + reviewCaseId.ToString("D") + (resolve ? "/resolve" : "/dismiss");
        probe.Arm();
        Task<HttpResponseMessage> winner = assignmentWins
            ? PostAssignmentAsync(client, assignmentPath, token, Guid.NewGuid())
            : PostCloseAsync(client, closePath, token, resolve);
        await probe.WaitForCaseReadsAsync(1);
        Task<HttpResponseMessage> loser = assignmentWins
            ? PostCloseAsync(client, closePath, token, resolve)
            : PostAssignmentAsync(client, assignmentPath, token, Guid.NewGuid());
        HttpResponseMessage[] responses = await Task.WhenAll(winner, loser);

        Assert.Equal(2, probe.SaveAttempts);
        Assert.Equal(1, probe.Commits);
        Assert.Equal(1, probe.ConcurrencyFailures);
        Assert.Equal(0, probe.NamedConflicts);
        Assert.Equal(outstanding, await OutstandingAsync(invoice.Id));
        Assert.Equal(commentsBefore, await CountAsync(context => context.ReviewCaseComments.CountAsync(comment => comment.OrganizationId == organizationId)));
        Assert.Equal(allocationsBefore, await CountAsync(context => context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId)));
        Assert.Equal(assignmentWins ? HttpStatusCode.Created : HttpStatusCode.OK, responses[0].StatusCode);
        CaseRow row = await ReadCaseAsync(reviewCaseId);
        Assert.NotEqual(token, row.RowVersion);
        if (assignmentWins)
        {
            Assert.Equal(historyBefore + 1, await HistoryCountAsync(organizationId));
            Assert.Equal(auditsBefore, await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId)));
            Assert.Equal(resolvedBefore, await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry =>
                entry.OrganizationId == organizationId && entry.Action == ReviewCaseAuditAction.Resolved)));
            Assert.Equal(dismissedBefore, await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry =>
                entry.OrganizationId == organizationId && entry.Action == ReviewCaseAuditAction.Dismissed)));
            Assert.Equal(ReviewCaseStatus.Open, row.Status);
            Assert.Equal(claim ? ReviewCaseActorSource.DevelopmentJwt : null, row.Source);
            Assert.Equal(claim ? "owner" : null, row.ActorId);
            await AssertProblemAsync(
                responses.Single(response => response.StatusCode == HttpStatusCode.Conflict),
                HttpStatusCode.Conflict,
                ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code);
        }
        else
        {
            Assert.Equal(historyBefore, await HistoryCountAsync(organizationId));
            Assert.Equal(auditsBefore + 1, await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId)));
            Assert.Equal(resolvedBefore + (resolve ? 1 : 0), await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry =>
                entry.OrganizationId == organizationId && entry.Action == ReviewCaseAuditAction.Resolved)));
            Assert.Equal(dismissedBefore + (resolve ? 0 : 1), await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry =>
                entry.OrganizationId == organizationId && entry.Action == ReviewCaseAuditAction.Dismissed)));
            Assert.Equal(resolve ? ReviewCaseStatus.Resolved : ReviewCaseStatus.Dismissed, row.Status);
            Assert.Equal(claim ? null : ReviewCaseActorSource.DevelopmentJwt, row.Source);
            Assert.Equal(claim ? null : "owner", row.ActorId);
            await AssertProblemAsync(
                responses.Single(response => response.StatusCode == HttpStatusCode.Conflict),
                HttpStatusCode.Conflict,
                ReconciliationApplicationErrors.ReviewCaseAssignmentConcurrencyConflict.Code);
        }
    }

    [Fact]
    public async Task A_shared_key_hits_the_idempotency_index_during_save_and_replays()
    {
        AssignmentRaceProbe probe = new(AssignmentRaceMode.OrderedSaveAfterSharedRead);
        using FinOpsDeskApiFactory factory = RaceFactory(probe);
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient first = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-a"));
        using HttpClient second = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-a"));
        Invoice invoice = await SeedInvoiceAsync(organizationId, "assignment-save-replay");
        Guid reviewCaseId = await OpenAsync(first, invoice.Id.Value);
        string token = await DetailTokenAsync(first, reviewCaseId);
        decimal outstanding = await OutstandingAsync(invoice.Id);
        Guid key = Guid.NewGuid();
        probe.Arm();

        Task<HttpResponseMessage> leader = PostAssignmentAsync(first, AssignmentPath(reviewCaseId), token, key);
        await probe.WaitForCaseReadsAsync(1);
        Task<HttpResponseMessage> follower = PostAssignmentAsync(second, AssignmentPath(reviewCaseId), token, key);
        HttpResponseMessage[] responses = await Task.WhenAll(leader, follower);

        Assert.Equal(2, probe.SaveAttempts);
        Assert.Equal(1, probe.Commits);
        Assert.Equal(0, probe.ConcurrencyFailures);
        Assert.Equal(1, probe.NamedConflicts);
        Assert.Equal(1, await HistoryCountAsync(organizationId));
        Assert.Equal(outstanding, await OutstandingAsync(invoice.Id));
        Assert.Equal("actor-a", (await ReadCaseAsync(reviewCaseId)).ActorId);
        HttpResponseMessage created = responses.Single(response => response.StatusCode == HttpStatusCode.Created);
        HttpResponseMessage replayed = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(await created.Content.ReadAsStringAsync(), await replayed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_failed_history_insert_does_not_keep_the_assignment()
    {
        using FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IReviewCaseAssignmentWriter>();
                services.AddScoped<IReviewCaseAssignmentWriter>(serviceProvider =>
                    new CorruptAssignmentWriter(
                        serviceProvider.GetRequiredService<ReviewCaseAssignmentStore>(),
                        serviceProvider.GetRequiredService<ReconciliationDbContext>()));
            });
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "assignment-atomic")).Id.Value);
        string token = await DetailTokenAsync(client, reviewCaseId);
        CaseRow before = await ReadCaseAsync(reviewCaseId);

        HttpResponseMessage response = await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), token, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        CaseRow after = await ReadCaseAsync(reviewCaseId);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Null(after.ActorId);
        Assert.Equal(ReviewCaseStatus.Open, after.Status);
        Assert.Equal(0, await HistoryCountAsync(organizationId));
    }

    [Fact]
    public async Task Sql_rejects_assignment_shape_foreign_keys_and_history_mutation()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "assignment-sql")).Id.Value);
        Assert.Equal(HttpStatusCode.Created, (await PostAssignmentAsync(client, AssignmentPath(reviewCaseId), await DetailTokenAsync(client, reviewCaseId), Guid.NewGuid())).StatusCode);
        ReviewCaseAssignmentEntry stored = await LoadSingleAsync(organizationId);
        CaseRow before = await ReadCaseAsync(reviewCaseId);

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (SqlCommand trigger = new(
            """
            SELECT COUNT(*)
            FROM sys.triggers
            WHERE name = N'TR_ReviewCaseAssignmentEntries_RejectMutation'
              AND is_disabled = 0
            """,
            connection))
        {
            Assert.Equal(1, (int)(await trigger.ExecuteScalarAsync())!);
        }

        SqlException updated = await ExecuteAsync(
            connection,
            "UPDATE [reconciliation].[ReviewCaseAssignmentEntries] SET [ActorId] = N'changed' WHERE [Id] = @id",
            ("@id", stored.Id.Value));
        SqlException deleted = await ExecuteAsync(
            connection,
            "DELETE FROM [reconciliation].[ReviewCaseAssignmentEntries] WHERE [Id] = @id",
            ("@id", stored.Id.Value));
        Assert.Equal(50003, updated.Number);
        Assert.Equal(50003, deleted.Number);

        SqlException partial = await ExecuteAsync(
            connection,
            "UPDATE [reconciliation].[ReviewCases] SET [AssignedActorId] = NULL WHERE [Id] = @id",
            ("@id", reviewCaseId));
        SqlException badSource = await ExecuteAsync(
            connection,
            "UPDATE [reconciliation].[ReviewCases] SET [AssignedActorSource] = 3 WHERE [Id] = @id",
            ("@id", reviewCaseId));
        Assert.Equal(547, partial.Number);
        Assert.Contains("CK_ReviewCases_Assignment", partial.Message, StringComparison.Ordinal);
        Assert.Equal(547, badSource.Number);

        SqlException releaseTime = await ExecuteAsync(
            connection,
            """
            INSERT INTO [reconciliation].[ReviewCaseAssignmentEntries]
                ([Id], [OrganizationId], [ReviewCaseId], [Operation], [ActorSource], [ActorId], [RequestConcurrencyToken], [IdempotencyKey], [RecordedAt], [AssignedAt])
            VALUES
                (@id, @organizationId, @reviewCaseId, 2, 1, N'actor', N'AAAAAAAAAAA=', @key, SYSUTCDATETIME(), SYSUTCDATETIME())
            """,
            ("@id", Guid.NewGuid()),
            ("@organizationId", organizationId.Value),
            ("@reviewCaseId", reviewCaseId),
            ("@key", Guid.NewGuid()));
        Assert.Equal(547, releaseTime.Number);
        Assert.Contains("CK_ReviewCaseAssignmentEntries_AssignedAt", releaseTime.Message, StringComparison.Ordinal);

        SqlException foreign = await ExecuteAsync(
            connection,
            """
            INSERT INTO [reconciliation].[ReviewCaseAssignmentEntries]
                ([Id], [OrganizationId], [ReviewCaseId], [Operation], [ActorSource], [ActorId], [RequestConcurrencyToken], [IdempotencyKey], [RecordedAt], [AssignedAt])
            VALUES
                (@id, @organizationId, @reviewCaseId, 1, 1, N'actor', N'AAAAAAAAAAA=', @key, SYSUTCDATETIME(), SYSUTCDATETIME())
            """,
            ("@id", Guid.NewGuid()),
            ("@organizationId", Guid.NewGuid()),
            ("@reviewCaseId", reviewCaseId),
            ("@key", Guid.NewGuid()));
        Assert.Equal(547, foreign.Number);

        Assert.Equal(before.RowVersion, (await ReadCaseAsync(reviewCaseId)).RowVersion);
        Assert.Equal("test-user", (await LoadSingleAsync(organizationId)).ActorId);
    }

    [Fact]
    public async Task Sql_assignment_check_accepts_only_all_null_or_a_complete_known_source()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "assignment-check")).Id.Value);
        DateTimeOffset assignedAt = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (SqlCommand definition = new(
            """
            SELECT [definition]
            FROM sys.check_constraints
            WHERE [name] = N'CK_ReviewCases_Assignment'
            """,
            connection))
        {
            string text = (string)(await definition.ExecuteScalarAsync())!;
            Assert.Contains("[AssignedActorSource] IS NOT NULL", text, StringComparison.Ordinal);
        }

        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE [reconciliation].[ReviewCases]
            SET [AssignedActorSource] = NULL, [AssignedActorId] = NULL, [AssignedAt] = NULL
            WHERE [Id] = @id
            """,
            ("@id", reviewCaseId));
        await AssertAssignmentColumnsAsync(connection, reviewCaseId, null, null, null);

        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE [reconciliation].[ReviewCases]
            SET [AssignedActorSource] = 1, [AssignedActorId] = N'owner', [AssignedAt] = @assignedAt
            WHERE [Id] = @id
            """,
            ("@id", reviewCaseId),
            ("@assignedAt", assignedAt));
        string completeVersion = (await ReadCaseAsync(reviewCaseId)).RowVersion;
        await AssertAssignmentColumnsAsync(connection, reviewCaseId, 1, "owner", assignedAt);

        (string Label, object Source, object ActorId, object AssignedAt)[] rejected =
        [
            ("source", DBNull.Value, "owner", assignedAt),
            ("actor", 1, DBNull.Value, assignedAt),
            ("time", 1, "owner", DBNull.Value),
            ("actor-and-time", DBNull.Value, DBNull.Value, assignedAt),
            ("source-and-time", DBNull.Value, "owner", DBNull.Value),
            ("source-and-actor", 1, DBNull.Value, DBNull.Value),
            ("unknown-source", 3, "owner", assignedAt),
        ];
        foreach ((string label, object source, object actorId, object rejectedAt) in rejected)
        {
            SqlException exception = await ExecuteAsync(
                connection,
                """
                UPDATE [reconciliation].[ReviewCases]
                SET [AssignedActorSource] = @source, [AssignedActorId] = @actorId, [AssignedAt] = @assignedAt
                WHERE [Id] = @id
                """,
                ("@id", reviewCaseId),
                ("@source", source),
                ("@actorId", actorId),
                ("@assignedAt", rejectedAt));
            Assert.True(
                exception.Number == 547
                && exception.Message.Contains("CK_ReviewCases_Assignment", StringComparison.Ordinal),
                label + " -> " + exception.Number + " " + exception.Message);
            await AssertAssignmentColumnsAsync(connection, reviewCaseId, 1, "owner", assignedAt);
            Assert.Equal(completeVersion, (await ReadCaseAsync(reviewCaseId)).RowVersion);
        }

        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE [reconciliation].[ReviewCases]
            SET [AssignedActorSource] = NULL, [AssignedActorId] = NULL, [AssignedAt] = NULL
            WHERE [Id] = @id
            """,
            ("@id", reviewCaseId));
        await AssertAssignmentColumnsAsync(connection, reviewCaseId, null, null, null);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private FinOpsDeskApiFactory RaceFactory(AssignmentRaceProbe probe)
    {
        return new FinOpsDeskApiFactory(
            _fixture.ConnectionString,
            services =>
            {
                services.AddScoped<ReviewCaseRepository>();
                services.RemoveAll<IReviewCaseRepository>();
                services.AddScoped<IReviewCaseRepository>(serviceProvider =>
                    new BarrierReviewCaseRepository(serviceProvider.GetRequiredService<ReviewCaseRepository>(), probe));
                services.RemoveAll<IReviewCaseAssignmentLookup>();
                services.AddScoped<IReviewCaseAssignmentLookup>(serviceProvider =>
                    new BarrierAssignmentLookup(serviceProvider.GetRequiredService<ReviewCaseAssignmentStore>(), probe));
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork>(serviceProvider =>
                    new SignalingUnitOfWork(serviceProvider.GetRequiredService<ReconciliationDbContext>(), probe));
            });
    }

    private HttpClient Client(OrganizationId organizationId)
    {
        return _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
    }

    private async Task<Invoice> SeedInvoiceAsync(OrganizationId organizationId, string number)
    {
        Invoice invoice = new(InvoiceId.New(), organizationId, number, new Money(25m, PersistenceTestData.Pln));
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }

    private async Task<CaseRow> ReadCaseAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        var row = await context.ReviewCases.AsNoTracking()
            .Where(reviewCase => reviewCase.Id == new ReviewCaseId(reviewCaseId))
            .Select(reviewCase => new
            {
                reviewCase.Status,
                reviewCase.AssignedActorSource,
                reviewCase.AssignedActorId,
                RowVersion = EF.Property<byte[]>(reviewCase, "RowVersion"),
            })
            .SingleAsync();
        return new CaseRow(
            row.Status,
            row.AssignedActorSource,
            row.AssignedActorId,
            Convert.ToBase64String(row.RowVersion));
    }

    private async Task<int> HistoryCountAsync(OrganizationId organizationId)
    {
        return await CountAsync(context => context.ReviewCaseAssignmentEntries.CountAsync(entry => entry.OrganizationId == organizationId));
    }

    private async Task<int> CountAsync(Func<ReconciliationDbContext, Task<int>> query)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await query(context);
    }

    private async Task<decimal> OutstandingAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.AsNoTracking().SingleAsync(candidate => candidate.Id == invoiceId);
        return invoice.OutstandingAmount.Amount;
    }

    private async Task<ReviewCaseAssignmentEntry> LoadSingleAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseAssignmentEntries.AsNoTracking().SingleAsync(entry => entry.OrganizationId == organizationId);
    }

    private static async Task<Guid> OpenAsync(HttpClient client, Guid invoiceId)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/review-cases")
        {
            Content = JsonContent(new { reason = "NeedsInvestigation", invoiceId }),
        };
        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reviewCaseId").GetGuid();
    }

    private static async Task ResolveAsync(HttpClient client, Guid reviewCaseId, string token)
    {
        HttpResponseMessage response = await PostCloseAsync(
            client,
            "/api/review-cases/" + reviewCaseId.ToString("D") + "/resolve",
            token,
            resolve: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task DismissAsync(HttpClient client, Guid reviewCaseId)
    {
        string token = await AssignmentTokenAsync(client, reviewCaseId);
        HttpResponseMessage response = await PostCloseAsync(
            client,
            "/api/review-cases/" + reviewCaseId.ToString("D") + "/dismiss",
            token,
            resolve: false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> DetailTokenAsync(HttpClient client, Guid reviewCaseId)
    {
        HttpResponseMessage response = await client.GetAsync("/api/review-cases/" + reviewCaseId.ToString("D"));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reviewCase").GetProperty("concurrencyToken").GetString()!;
    }

    private static async Task<string> AssignmentTokenAsync(HttpClient client, Guid reviewCaseId)
    {
        HttpResponseMessage response = await client.GetAsync(AssignmentPath(reviewCaseId));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("concurrencyToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostAssignmentAsync(
        HttpClient client,
        string path,
        string token,
        Guid idempotencyKey)
    {
        return await PostRawAsync(
            client,
            path,
            "{\"concurrencyToken\":" + JsonSerializer.Serialize(token) + "}",
            idempotencyKey.ToString("D"));
    }

    private static async Task<HttpResponseMessage> PostCloseAsync(
        HttpClient client,
        string path,
        string token,
        bool resolve)
    {
        string json = resolve
            ? "{\"outcome\":\"NoActionRequired\",\"concurrencyToken\":" + JsonSerializer.Serialize(token) + "}"
            : "{\"concurrencyToken\":" + JsonSerializer.Serialize(token) + "}";
        return await PostRawAsync(client, path, json, null);
    }

    private static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        string path,
        string json,
        string? idempotencyKey,
        string mediaType = "application/json")
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, mediaType),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static StringContent JsonContent(object body)
    {
        return new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
    }

    private static string AssignmentPath(Guid reviewCaseId)
    {
        return "/api/review-cases/" + reviewCaseId.ToString("D") + "/assignment";
    }

    private static string ReleasePath(Guid reviewCaseId)
    {
        return AssignmentPath(reviewCaseId) + "/release";
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(document.RootElement.TryGetProperty("concurrencyToken", out _));
    }

    private static async Task<SqlException> ExecuteAsync(
        SqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    private static async Task ExecuteNonQueryAsync(
        SqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertAssignmentColumnsAsync(
        SqlConnection connection,
        Guid reviewCaseId,
        int? source,
        string? actorId,
        DateTimeOffset? assignedAt)
    {
        await using SqlCommand command = new(
            """
            SELECT [AssignedActorSource], [AssignedActorId], [AssignedAt]
            FROM [reconciliation].[ReviewCases]
            WHERE [Id] = @id
            """,
            connection);
        command.Parameters.AddWithValue("@id", reviewCaseId);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(source, reader.IsDBNull(0) ? null : reader.GetInt32(0));
        Assert.Equal(actorId, reader.IsDBNull(1) ? null : reader.GetString(1));
        Assert.Equal(assignedAt, reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2));
    }

    private sealed record CaseRow(
        ReviewCaseStatus Status,
        ReviewCaseActorSource? Source,
        string? ActorId,
        string RowVersion);

    private sealed class FixedAttribution : ICurrentActorAttribution
    {
        private readonly ReviewCaseActorSource _source;
        private readonly string _actorId;

        public FixedAttribution(ReviewCaseActorSource source, string actorId)
        {
            _source = source;
            _actorId = actorId;
        }

        public bool TryGet(out ReviewCaseActorSource source, out string actorId)
        {
            source = _source;
            actorId = _actorId;
            return true;
        }
    }

    private sealed class CorruptAssignmentWriter : IReviewCaseAssignmentWriter
    {
        private readonly ReviewCaseAssignmentStore _store;
        private readonly ReconciliationDbContext _dbContext;

        public CorruptAssignmentWriter(ReviewCaseAssignmentStore store, ReconciliationDbContext dbContext)
        {
            _store = store;
            _dbContext = dbContext;
        }

        public void Add(ReviewCaseAssignmentEntry entry)
        {
            _store.Add(entry);
            _dbContext.Entry(entry).Property(item => item.ActorId).CurrentValue = " bad";
        }
    }

    private enum AssignmentRaceMode
    {
        HoldSecondMissUntilSave,
        ReleaseAfterTwoMisses,
        ReleaseAfterTwoCaseReads,
        WinnerThenLoserReads,
        OrderedSaveAfterSharedRead,
    }

    private sealed class AssignmentRaceProbe
    {
        private readonly AssignmentRaceMode _mode;
        private TaskCompletionSource _secondMiss = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _secondRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _firstMiss = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _misses;
        private int _caseReads;
        private int _commits;
        private int _saveAttempts;
        private int _concurrencyFailures;
        private int _namedConflicts;

        public AssignmentRaceProbe(AssignmentRaceMode mode)
        {
            _mode = mode;
        }

        public int Commits => Volatile.Read(ref _commits);

        public int SaveAttempts => Volatile.Read(ref _saveAttempts);

        public int ConcurrencyFailures => Volatile.Read(ref _concurrencyFailures);

        public int NamedConflicts => Volatile.Read(ref _namedConflicts);

        public bool DelayLoad =>
            _mode == AssignmentRaceMode.WinnerThenLoserReads && Volatile.Read(ref _armed) == 1;

        public void Arm()
        {
            _secondMiss = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstMiss = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _commits, 0);
            Volatile.Write(ref _saveAttempts, 0);
            Volatile.Write(ref _concurrencyFailures, 0);
            Volatile.Write(ref _namedConflicts, 0);
            Volatile.Write(ref _misses, 0);
            Volatile.Write(ref _caseReads, 0);
            Volatile.Write(ref _armed, 1);
        }

        public Task WaitForMissesAsync(int count)
        {
            if (count != 1)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            return _firstMiss.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public Task WaitForCaseReadsAsync(int count)
        {
            if (count != 1)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            return _firstRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async Task HoldMissAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _armed) == 0
                || _mode is AssignmentRaceMode.ReleaseAfterTwoCaseReads
                    or AssignmentRaceMode.WinnerThenLoserReads
                    or AssignmentRaceMode.OrderedSaveAfterSharedRead)
            {
                return;
            }

            int seen = Interlocked.Increment(ref _misses);
            if (seen > 2)
            {
                return;
            }

            if (_mode == AssignmentRaceMode.ReleaseAfterTwoMisses)
            {
                if (seen == 2)
                {
                    _secondMiss.TrySetResult();
                }

                await _secondMiss.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return;
            }

            if (seen == 1)
            {
                _firstMiss.TrySetResult();
                await _secondMiss.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return;
            }

            _secondMiss.TrySetResult();
            await _saved.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        public async Task HoldCaseReadAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                return;
            }

            int seen = Interlocked.Increment(ref _caseReads);
            if (_mode == AssignmentRaceMode.OrderedSaveAfterSharedRead)
            {
                if (seen == 1)
                {
                    _firstRead.TrySetResult();
                    await _secondRead.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                    return;
                }

                if (seen == 2)
                {
                    _secondRead.TrySetResult();
                    await _saved.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                }

                return;
            }

            if (_mode == AssignmentRaceMode.ReleaseAfterTwoCaseReads)
            {
                if (seen == 2)
                {
                    _secondRead.TrySetResult();
                }

                if (seen <= 2)
                {
                    await _secondRead.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                }

                return;
            }

            if (_mode != AssignmentRaceMode.WinnerThenLoserReads || seen > 2)
            {
                return;
            }

            if (seen == 1)
            {
                _firstRead.TrySetResult();
                await _secondRead.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return;
            }

            _secondRead.TrySetResult();
            await _saved.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        public void NoteSaveAttempt()
        {
            Interlocked.Increment(ref _saveAttempts);
        }

        public void NoteSaved()
        {
            Interlocked.Increment(ref _commits);
            _saved.TrySetResult();
        }

        public void NoteConcurrency()
        {
            Interlocked.Increment(ref _concurrencyFailures);
        }

        public void NoteNamed()
        {
            Interlocked.Increment(ref _namedConflicts);
        }
    }

    private sealed class BarrierAssignmentLookup : IReviewCaseAssignmentLookup
    {
        private readonly ReviewCaseAssignmentStore _store;
        private readonly AssignmentRaceProbe _probe;

        public BarrierAssignmentLookup(ReviewCaseAssignmentStore store, AssignmentRaceProbe probe)
        {
            _store = store;
            _probe = probe;
        }

        public async Task<ReviewCaseAssignmentEntry?> FindByIdempotencyKeyAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            ReviewCaseAssignmentEntry? found = await _store.FindByIdempotencyKeyAsync(
                organizationId,
                idempotencyKey,
                cancellationToken);
            if (found is null)
            {
                await _probe.HoldMissAsync(cancellationToken);
            }

            return found;
        }
    }

    private sealed class BarrierReviewCaseRepository : IReviewCaseRepository
    {
        private readonly ReviewCaseRepository _inner;
        private readonly AssignmentRaceProbe _probe;

        public BarrierReviewCaseRepository(ReviewCaseRepository inner, AssignmentRaceProbe probe)
        {
            _inner = inner;
            _probe = probe;
        }

        public void Add(ReviewCase reviewCase)
        {
            _inner.Add(reviewCase);
        }

        public async Task<ReviewCase?> GetByIdAsync(
            OrganizationId organizationId,
            ReviewCaseId reviewCaseId,
            CancellationToken cancellationToken)
        {
            if (_probe.DelayLoad)
            {
                await _probe.HoldCaseReadAsync(cancellationToken);
                return await _inner.GetByIdAsync(organizationId, reviewCaseId, cancellationToken);
            }

            ReviewCase? found = await _inner.GetByIdAsync(organizationId, reviewCaseId, cancellationToken);
            await _probe.HoldCaseReadAsync(cancellationToken);
            return found;
        }

        public byte[] GetRowVersion(ReviewCase reviewCase)
        {
            return _inner.GetRowVersion(reviewCase);
        }

        public void ExpectRowVersion(ReviewCase reviewCase, byte[] rowVersion)
        {
            _inner.ExpectRowVersion(reviewCase, rowVersion);
        }
    }

    private sealed class SignalingUnitOfWork : IUnitOfWork
    {
        private readonly ReconciliationDbContext _inner;
        private readonly AssignmentRaceProbe _probe;
        private readonly ReviewCaseAssignmentConflict _conflict = new();

        public SignalingUnitOfWork(ReconciliationDbContext inner, AssignmentRaceProbe probe)
        {
            _inner = inner;
            _probe = probe;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            _probe.NoteSaveAttempt();
            try
            {
                await ((IUnitOfWork)_inner).SaveChangesAsync(cancellationToken);
                _probe.NoteSaved();
            }
            catch (ConcurrencyConflictException)
            {
                _probe.NoteConcurrency();
                throw;
            }
            catch (UniqueConstraintConflictException exception) when (_conflict.IsIdempotencyKeyConflict(exception))
            {
                _probe.NoteNamed();
                throw;
            }
        }
    }
}