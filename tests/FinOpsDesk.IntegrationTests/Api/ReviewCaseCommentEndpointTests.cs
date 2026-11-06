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
public sealed class ReviewCaseCommentEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ReviewCaseCommentEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Comment_can_be_appended_to_open_resolved_and_dismissed_cases_without_changing_them()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Invoice openInvoice = await SeedInvoiceAsync(organizationId, "comment-open");
        Invoice resolvedInvoice = await SeedInvoiceAsync(organizationId, "comment-resolved");
        Invoice dismissedInvoice = await SeedInvoiceAsync(organizationId, "comment-dismissed");
        Guid openId = await OpenAsync(client, openInvoice.Id.Value);
        Guid resolvedId = await OpenAsync(client, resolvedInvoice.Id.Value);
        Guid dismissedId = await OpenAsync(client, dismissedInvoice.Id.Value);
        await ResolveAsync(client, resolvedId);
        await DismissAsync(client, dismissedId);
        CaseState openBefore = await ReadCaseAsync(openId);
        CaseState resolvedBefore = await ReadCaseAsync(resolvedId);
        CaseState dismissedBefore = await ReadCaseAsync(dismissedId);
        decimal openOutstanding = await OutstandingAsync(openInvoice.Id);
        int audits = await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        int financialAudits = await CountAsync(context => context.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        int allocations = await CountAsync(context => context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));

        HttpResponseMessage openComment = await PostCommentAsync(client, openId, "<b>open</b>", Guid.NewGuid());
        HttpResponseMessage resolvedComment = await PostCommentAsync(client, resolvedId, "resolved note", Guid.NewGuid());
        HttpResponseMessage dismissedComment = await PostCommentAsync(client, dismissedId, "dismissed note", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Created, openComment.StatusCode);
        Assert.Null(openComment.Headers.Location);
        Assert.Equal(HttpStatusCode.Created, resolvedComment.StatusCode);
        Assert.Equal(HttpStatusCode.Created, dismissedComment.StatusCode);
        Assert.Equal(openBefore, await ReadCaseAsync(openId));
        Assert.Equal(resolvedBefore, await ReadCaseAsync(resolvedId));
        Assert.Equal(dismissedBefore, await ReadCaseAsync(dismissedId));
        Assert.Equal(openOutstanding, await OutstandingAsync(openInvoice.Id));
        Assert.Equal(audits, await CountAsync(context => context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId)));
        Assert.Equal(financialAudits, await CountAsync(context => context.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId)));
        Assert.Equal(allocations, await CountAsync(context => context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId)));
        using JsonDocument created = JsonDocument.Parse(await openComment.Content.ReadAsStringAsync());
        Assert.Equal("<b>open</b>", created.RootElement.GetProperty("text").GetString());
        Assert.Equal("test-user", created.RootElement.GetProperty("actorId").GetString());
        Assert.False(created.RootElement.TryGetProperty("organizationId", out _));
    }

    [Fact]
    public async Task Text_is_trimmed_and_a_byte_order_mark_is_preserved()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "comment-feff")).Id.Value);
        string text = "  \uFEFFkept\uFEFF  ";

        HttpResponseMessage response = await PostCommentAsync(client, reviewCaseId, text, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("\uFEFFkept\uFEFF", document.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task Whitespace_only_text_is_rejected(string text)
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "comment-blank")).Id.Value);

        await AssertProblemAsync(
            await PostCommentAsync(client, reviewCaseId, text, Guid.NewGuid()),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code);
        Assert.Equal(0, await CommentCountAsync(organizationId));
    }

    [Fact]
    public async Task Text_length_accepts_500_and_rejects_501_utf16_units()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "comment-length")).Id.Value);

        string trimmed = new string('a', 500);
        HttpResponseMessage accepted = await PostCommentAsync(
            client,
            reviewCaseId,
            "  " + trimmed + "   ",
            Guid.NewGuid());
        string acceptedBody = await accepted.Content.ReadAsStringAsync();
        Assert.True(accepted.StatusCode == HttpStatusCode.Created, acceptedBody);
        using JsonDocument created = JsonDocument.Parse(acceptedBody);
        Assert.Equal(trimmed, created.RootElement.GetProperty("text").GetString());
        Assert.Equal(trimmed, (await LoadSingleAsync(organizationId)).Text);
        await AssertProblemAsync(
            await PostCommentAsync(client, reviewCaseId, " " + new string('b', 501) + " ", Guid.NewGuid()),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code);
        Assert.Equal(1, await CommentCountAsync(organizationId));
    }

    [Fact]
    public async Task Invalid_comment_requests_use_the_comment_codes_and_existing_transport_codes()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "comment-invalid")).Id.Value);
        string path = CommentsPath(reviewCaseId);

        await AssertProblemAsync(
            await PostRawAsync(client, path, "{}", Guid.NewGuid().ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, path, "{\"text\":null}", Guid.NewGuid().ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, path, "{\"text\":1}", Guid.NewGuid().ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, path, "{\"text\":\"kept\",\"note\":\"extra\"}", Guid.NewGuid().ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, path, "{", Guid.NewGuid().ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, path, "{\"text\":\"kept\"}", null),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyRequired.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, path, "{\"text\":\"kept\"}", "  " + Guid.NewGuid().ToString("D") + "  "),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, path, "{\"text\":\"kept\"}", Guid.Empty.ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, path, "{\"text\":\"kept\"}", Guid.NewGuid().ToString("N")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, path + "?organizationId=" + organizationId.Value.ToString("D"), "{\"text\":\"kept\"}", Guid.NewGuid().ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code);

        using HttpRequestMessage media = new(HttpMethod.Post, path)
        {
            Content = new StringContent("{\"text\":\"kept\"}", Encoding.UTF8, "text/plain"),
        };
        media.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        await AssertProblemAsync(
            await client.SendAsync(media),
            HttpStatusCode.UnsupportedMediaType,
            ReconciliationApplicationErrors.ReviewCaseUnsupportedMediaType.Code);

        string oversized = "{\"text\":\"" + new string('x', 4100) + "\"}";
        await AssertProblemAsync(
            await PostRawAsync(client, path, oversized, Guid.NewGuid().ToString("D")),
            HttpStatusCode.RequestEntityTooLarge,
            ReconciliationApplicationErrors.ReviewCaseRequestTooLarge.Code);
        Assert.Equal(0, await CommentCountAsync(organizationId));
    }

    [Fact]
    public async Task Matching_replay_preserves_the_original_comment_and_a_new_key_appends_another()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient original = _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-original"));
        using HttpClient later = _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "actor-later"));
        Guid reviewCaseId = await OpenAsync(original, (await SeedInvoiceAsync(organizationId, "comment-replay")).Id.Value);
        Guid key = Guid.NewGuid();

        HttpResponseMessage created = await PostCommentAsync(original, reviewCaseId, "  kept  ", key);
        HttpResponseMessage replayed = await PostCommentAsync(later, reviewCaseId, "kept", key);
        HttpResponseMessage another = await PostCommentAsync(later, reviewCaseId, "kept", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Null(replayed.Headers.Location);
        Assert.Equal(HttpStatusCode.Created, another.StatusCode);
        using JsonDocument first = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        using JsonDocument second = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync());
        using JsonDocument third = JsonDocument.Parse(await another.Content.ReadAsStringAsync());
        Assert.Equal(first.RootElement.GetProperty("id").GetGuid(), second.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("kept", second.RootElement.GetProperty("text").GetString());
        Assert.Equal("actor-original", second.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(first.RootElement.GetProperty("recordedAt").GetString(), second.RootElement.GetProperty("recordedAt").GetString());
        Assert.NotEqual(first.RootElement.GetProperty("id").GetGuid(), third.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("actor-later", third.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(2, await CommentCountAsync(organizationId));
    }

    [Fact]
    public async Task A_different_payload_conflicts_even_when_the_requested_case_is_unknown()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "comment-conflict")).Id.Value);
        Guid key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostCommentAsync(client, reviewCaseId, "kept", key)).StatusCode);

        await AssertProblemAsync(
            await PostCommentAsync(client, reviewCaseId, "other", key),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyPayloadConflict.Code);
        await AssertProblemAsync(
            await PostCommentAsync(client, Guid.NewGuid(), "kept", key),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyPayloadConflict.Code);
        Assert.Equal(1, await CommentCountAsync(organizationId));
    }

    [Fact]
    public async Task Both_requests_miss_the_key_before_a_matching_insert_and_one_named_collision_recovers_it()
    {
        await AssertCollisionAsync(
            sameCase: true,
            sameText: true,
            expectedSecondStatus: HttpStatusCode.OK,
            conflictCode: null);
    }

    [Fact]
    public async Task Both_requests_miss_the_key_before_different_text_collides_on_the_named_index()
    {
        await AssertCollisionAsync(
            sameCase: true,
            sameText: false,
            expectedSecondStatus: HttpStatusCode.Conflict,
            conflictCode: ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyPayloadConflict.Code);
    }

    [Fact]
    public async Task Both_requests_miss_the_key_before_different_cases_collide_on_the_named_index()
    {
        await AssertCollisionAsync(
            sameCase: false,
            sameText: true,
            expectedSecondStatus: HttpStatusCode.Conflict,
            conflictCode: ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyPayloadConflict.Code);
    }

    [Fact]
    public async Task The_same_key_is_isolated_between_organizations()
    {
        OrganizationId firstOrganization = OrganizationId.New();
        OrganizationId secondOrganization = OrganizationId.New();
        using HttpClient first = _factory.CreateApiClient(TestAccessTokens.CreateForActor(firstOrganization.Value, "actor-a"));
        using HttpClient second = _factory.CreateApiClient(TestAccessTokens.CreateForActor(secondOrganization.Value, "actor-b"));
        Guid firstCase = await OpenAsync(first, (await SeedInvoiceAsync(firstOrganization, "comment-org-a")).Id.Value);
        Guid secondCase = await OpenAsync(second, (await SeedInvoiceAsync(secondOrganization, "comment-org-b")).Id.Value);
        Guid key = Guid.NewGuid();

        HttpResponseMessage createdA = await PostCommentAsync(first, firstCase, "alpha", key);
        HttpResponseMessage createdB = await PostCommentAsync(second, secondCase, "beta", key);
        HttpResponseMessage replayA = await PostCommentAsync(first, firstCase, "alpha", key);
        HttpResponseMessage replayB = await PostCommentAsync(second, secondCase, "beta", key);

        Assert.Equal(HttpStatusCode.Created, createdA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayB.StatusCode);
        using JsonDocument firstCreated = JsonDocument.Parse(await createdA.Content.ReadAsStringAsync());
        using JsonDocument secondCreated = JsonDocument.Parse(await createdB.Content.ReadAsStringAsync());
        using JsonDocument firstReplay = JsonDocument.Parse(await replayA.Content.ReadAsStringAsync());
        using JsonDocument secondReplay = JsonDocument.Parse(await replayB.Content.ReadAsStringAsync());
        Assert.NotEqual(firstCreated.RootElement.GetProperty("id").GetGuid(), secondCreated.RootElement.GetProperty("id").GetGuid());
        AssertSameComment(firstCreated.RootElement, firstReplay.RootElement);
        AssertSameComment(secondCreated.RootElement, secondReplay.RootElement);
        Assert.Equal("actor-a", firstReplay.RootElement.GetProperty("actorId").GetString());
        Assert.Equal("actor-b", secondReplay.RootElement.GetProperty("actorId").GetString());
        ReviewCaseComment storedA = await LoadSingleAsync(firstOrganization);
        ReviewCaseComment storedB = await LoadSingleAsync(secondOrganization);
        Assert.Equal("alpha", storedA.Text);
        Assert.Equal("beta", storedB.Text);
        Assert.Equal(firstCreated.RootElement.GetProperty("id").GetGuid(), storedA.Id.Value);
        Assert.Equal(secondCreated.RootElement.GetProperty("id").GetGuid(), storedB.Id.Value);
        Assert.Equal(1, await CommentCountAsync(firstOrganization));
        Assert.Equal(1, await CommentCountAsync(secondOrganization));

        HttpResponseMessage foreignList = await second.GetAsync(CommentsPath(firstCase));
        string foreignBody = await foreignList.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, foreignList.StatusCode);
        Assert.DoesNotContain("alpha", foreignBody, StringComparison.Ordinal);
        Assert.DoesNotContain("actor-a", foreignBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_actor_rejects_a_new_comment_and_a_replay()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient actor = Client(organizationId);
        using HttpClient missing = _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, null));
        Guid reviewCaseId = await OpenAsync(actor, (await SeedInvoiceAsync(organizationId, "comment-actor")).Id.Value);
        Guid key = Guid.NewGuid();

        await AssertProblemAsync(
            await PostCommentAsync(missing, reviewCaseId, "kept", Guid.NewGuid()),
            HttpStatusCode.Forbidden,
            ReconciliationApplicationErrors.ActorRequired.Code);
        Assert.Equal(HttpStatusCode.Created, (await PostCommentAsync(actor, reviewCaseId, "kept", key)).StatusCode);
        await AssertProblemAsync(
            await PostCommentAsync(missing, reviewCaseId, "kept", key),
            HttpStatusCode.Forbidden,
            ReconciliationApplicationErrors.ActorRequired.Code);
        Assert.Equal(1, await CommentCountAsync(organizationId));
    }

    [Fact]
    public async Task Unknown_and_foreign_cases_share_not_found_and_an_empty_case_returns_an_empty_page()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        using HttpClient ownerClient = Client(owner);
        using HttpClient otherClient = Client(other);
        Guid reviewCaseId = await OpenAsync(ownerClient, (await SeedInvoiceAsync(owner, "comment-empty")).Id.Value);

        HttpResponseMessage empty = await ownerClient.GetAsync(CommentsPath(reviewCaseId));
        string emptyBody = await empty.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using JsonDocument page = JsonDocument.Parse(emptyBody);
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, page.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt64());

        await AssertProblemAsync(
            await ownerClient.GetAsync(CommentsPath(Guid.NewGuid())),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code);
        await AssertProblemAsync(
            await otherClient.GetAsync(CommentsPath(reviewCaseId)),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code);
        await AssertProblemAsync(
            await PostCommentAsync(otherClient, reviewCaseId, "foreign", Guid.NewGuid()),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code);
    }

    [Fact]
    public async Task Comments_are_ordered_by_time_then_id_and_paged_without_a_shared_snapshot_contract()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            (await SeedInvoiceAsync(organizationId, "comment-page")).Id,
            null,
            null,
            null,
            new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
        DateTimeOffset sameTime = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        ReviewCaseComment first = ReviewCaseComment.Create(
            ReviewCaseCommentId.New(),
            organizationId,
            reviewCase.Id,
            "earlier",
            "actor-a",
            sameTime.AddMinutes(-1),
            Guid.NewGuid());
        ReviewCaseComment left = ReviewCaseComment.Create(
            ReviewCaseCommentId.New(),
            organizationId,
            reviewCase.Id,
            "same-left",
            "actor-a",
            sameTime,
            Guid.NewGuid());
        ReviewCaseComment right = ReviewCaseComment.Create(
            ReviewCaseCommentId.New(),
            organizationId,
            reviewCase.Id,
            "same-right",
            "actor-a",
            sameTime,
            Guid.NewGuid());
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.ReviewCases.Add(reviewCase);
            context.ReviewCaseComments.AddRange(right, first, left);
            await context.SaveChangesAsync();
        }

        Guid[] expected = await OrderedIdsAsync(organizationId, reviewCase.Id);
        using JsonDocument page = JsonDocument.Parse(await (await client.GetAsync(CommentsPath(reviewCase.Id.Value) + "?pageSize=2")).Content.ReadAsStringAsync());
        Assert.Equal(1, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, page.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, page.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(expected[0], page.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(expected[1], page.RootElement.GetProperty("items")[1].GetProperty("id").GetGuid());
        using JsonDocument next = JsonDocument.Parse(await (await client.GetAsync(CommentsPath(reviewCase.Id.Value) + "?page=2&pageSize=2")).Content.ReadAsStringAsync());
        Assert.Equal(1, next.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(expected[2], next.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(3, next.RootElement.GetProperty("totalCount").GetInt64());

        await AssertProblemAsync(
            await client.GetAsync(CommentsPath(reviewCase.Id.Value) + "?pageSize=101"),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidPagination.Code);
        await AssertProblemAsync(
            await client.GetAsync(CommentsPath(reviewCase.Id.Value) + "?page=21474838&pageSize=100"),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidPagination.Code);
        await AssertProblemAsync(
            await client.GetAsync(CommentsPath(reviewCase.Id.Value) + "?page=1&page=2"),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidPagination.Code);
    }

    [Fact]
    public async Task Sql_rejects_mutation_a_foreign_case_and_classifies_only_the_named_unique_index()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, (await SeedInvoiceAsync(organizationId, "comment-sql")).Id.Value);
        Assert.Equal(HttpStatusCode.Created, (await PostCommentAsync(client, reviewCaseId, "kept", Guid.NewGuid())).StatusCode);
        ReviewCaseComment stored = await LoadSingleAsync(organizationId);

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (SqlCommand trigger = new(
            """
            SELECT COUNT(*)
            FROM sys.triggers
            WHERE name = N'TR_ReviewCaseComments_RejectMutation'
              AND is_disabled = 0
            """,
            connection))
        {
            Assert.Equal(1, (int)(await trigger.ExecuteScalarAsync())!);
        }

        SqlException updated = await ExecuteAsync(
            connection,
            "UPDATE [reconciliation].[ReviewCaseComments] SET [Text] = N'changed' WHERE [Id] = @id",
            ("@id", stored.Id.Value));
        SqlException deleted = await ExecuteAsync(
            connection,
            "DELETE FROM [reconciliation].[ReviewCaseComments] WHERE [Id] = @id",
            ("@id", stored.Id.Value));
        Assert.Equal(50003, updated.Number);
        Assert.Equal(50003, deleted.Number);
        Assert.Equal("kept", (await LoadSingleAsync(organizationId)).Text);

        SqlException foreignCase = await ExecuteAsync(
            connection,
            """
            INSERT INTO [reconciliation].[ReviewCaseComments]
                ([Id], [OrganizationId], [ReviewCaseId], [Text], [ActorId], [RecordedAt], [IdempotencyKey])
            VALUES
                (@id, @organizationId, @reviewCaseId, N'x', N'actor', SYSUTCDATETIME(), @key)
            """,
            ("@id", Guid.NewGuid()),
            ("@organizationId", Guid.NewGuid()),
            ("@reviewCaseId", reviewCaseId),
            ("@key", Guid.NewGuid()));
        Assert.Equal(547, foreignCase.Number);

        ReviewCaseComment duplicateKey = ReviewCaseComment.Create(
            ReviewCaseCommentId.New(),
            organizationId,
            new ReviewCaseId(reviewCaseId),
            "other",
            "actor-b",
            DateTimeOffset.UtcNow,
            stored.IdempotencyKey);
        DbUpdateException keyConflict = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await using ReconciliationDbContext context = _fixture.CreateContext();
            context.ReviewCaseComments.Add(duplicateKey);
            await context.SaveChangesAsync();
        });
        Assert.True(new ReviewCaseCommentConflict().IsIdempotencyKeyConflict(keyConflict));

        ReviewCaseComment duplicateId = ReviewCaseComment.Create(
            stored.Id,
            organizationId,
            new ReviewCaseId(reviewCaseId),
            "other-id",
            "actor-b",
            DateTimeOffset.UtcNow,
            Guid.NewGuid());
        DbUpdateException primaryKey = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await using ReconciliationDbContext context = _fixture.CreateContext();
            context.ReviewCaseComments.Add(duplicateId);
            await context.SaveChangesAsync();
        });
        Assert.False(new ReviewCaseCommentConflict().IsIdempotencyKeyConflict(primaryKey));
        Assert.Equal(1, await CommentCountAsync(organizationId));
    }

    public void Dispose()
    {
        _factory.Dispose();
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

    private static async Task<Guid> OpenAsync(HttpClient client, Guid invoiceId)
    {
        HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/review-cases",
            new { reason = "NeedsInvestigation", invoiceId });
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reviewCaseId").GetGuid();
    }

    private static async Task ResolveAsync(HttpClient client, Guid reviewCaseId)
    {
        string token = await TokenAsync(client, reviewCaseId);
        HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/review-cases/" + reviewCaseId.ToString("D") + "/resolve",
            new { outcome = "NoActionRequired", concurrencyToken = token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task DismissAsync(HttpClient client, Guid reviewCaseId)
    {
        string token = await TokenAsync(client, reviewCaseId);
        HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/review-cases/" + reviewCaseId.ToString("D") + "/dismiss",
            new { concurrencyToken = token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> TokenAsync(HttpClient client, Guid reviewCaseId)
    {
        HttpResponseMessage response = await client.GetAsync("/api/review-cases/" + reviewCaseId.ToString("D"));
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reviewCase").GetProperty("concurrencyToken").GetString()!;
    }

    private async Task<CaseState> ReadCaseAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        var row = await context.ReviewCases
            .AsNoTracking()
            .Where(reviewCase => reviewCase.Id == new ReviewCaseId(reviewCaseId))
            .Select(reviewCase => new
            {
                reviewCase.Status,
                reviewCase.Outcome,
                reviewCase.ClosedAt,
                RowVersion = EF.Property<byte[]>(reviewCase, "RowVersion"),
            })
            .SingleAsync();
        return new CaseState(row.Status, row.Outcome, row.ClosedAt, Convert.ToBase64String(row.RowVersion));
    }

    private async Task<decimal> OutstandingAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.AsNoTracking().SingleAsync(candidate => candidate.Id == invoiceId);
        return invoice.OutstandingAmount.Amount;
    }

    private async Task<int> CommentCountAsync(OrganizationId organizationId)
    {
        return await CountAsync(context => context.ReviewCaseComments.CountAsync(comment => comment.OrganizationId == organizationId));
    }

    private async Task<int> CountAsync(Func<ReconciliationDbContext, Task<int>> query)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await query(context);
    }

    private async Task<ReviewCaseComment> LoadSingleAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseComments.AsNoTracking().SingleAsync(comment => comment.OrganizationId == organizationId);
    }

    private async Task<Guid[]> OrderedIdsAsync(OrganizationId organizationId, ReviewCaseId reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseComments
            .AsNoTracking()
            .Where(comment => comment.OrganizationId == organizationId && comment.ReviewCaseId == reviewCaseId)
            .OrderBy(comment => comment.RecordedAt)
            .ThenBy(comment => comment.Id)
            .Select(comment => comment.Id.Value)
            .ToArrayAsync();
    }

    private static async Task<HttpResponseMessage> PostCommentAsync(
        HttpClient client,
        Guid reviewCaseId,
        string text,
        Guid idempotencyKey)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, CommentsPath(reviewCaseId))
        {
            Content = JsonContent(new { text }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent(body),
        };
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        string path,
        string json,
        string? idempotencyKey)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
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

    private static string CommentsPath(Guid reviewCaseId)
    {
        return "/api/review-cases/" + reviewCaseId.ToString("D") + "/comments";
    }

    private async Task AssertCollisionAsync(
        bool sameCase,
        bool sameText,
        HttpStatusCode expectedSecondStatus,
        string? conflictCode)
    {
        OrganizationId organizationId = OrganizationId.New();
        CommentCollisionProbe probe = new();
        using FinOpsDeskApiFactory factory = new(
            _fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IReviewCaseCommentLookup>();
                services.AddScoped<IReviewCaseCommentLookup>(serviceProvider =>
                    new BarrierCommentLookup(
                        serviceProvider.GetRequiredService<ReviewCaseCommentStore>(),
                        probe));
                services.RemoveAll<IReviewCaseCommentConflict>();
                services.AddSingleton<IReviewCaseCommentConflict>(probe);
            });
        using HttpClient first = factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, "actor-a"));
        using HttpClient second = factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, "actor-b"));
        Guid leftCase = await OpenAsync(first, (await SeedInvoiceAsync(organizationId, "comment-collision-a")).Id.Value);
        Guid rightCase = sameCase
            ? leftCase
            : await OpenAsync(first, (await SeedInvoiceAsync(organizationId, "comment-collision-b")).Id.Value);
        Guid key = Guid.NewGuid();
        string leftText = "same-text";
        string rightText = sameText ? leftText : "other-text";

        HttpResponseMessage[] responses = await Task.WhenAll(
            PostCommentAsync(first, leftCase, leftText, key),
            PostCommentAsync(second, rightCase, rightText, key));

        Assert.Equal(2, probe.InitialMisses);
        Assert.Equal(1, probe.NamedConflicts);
        Assert.Equal(1, await CommentCountAsync(organizationId));
        HttpResponseMessage created = responses.Single(response => response.StatusCode == HttpStatusCode.Created);
        HttpResponseMessage other = responses.Single(response => response != created);
        Assert.Equal(expectedSecondStatus, other.StatusCode);
        Assert.Null(created.Headers.Location);
        using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        JsonElement createdComment = createdDocument.RootElement;
        ReviewCaseComment stored = await LoadSingleAsync(organizationId);
        Assert.Equal(createdComment.GetProperty("id").GetGuid(), stored.Id.Value);
        Assert.Equal(createdComment.GetProperty("text").GetString(), stored.Text);
        Assert.Equal(createdComment.GetProperty("actorId").GetString(), stored.ActorId);
        Assert.Equal(createdComment.GetProperty("recordedAt").GetDateTimeOffset(), stored.RecordedAt);
        Assert.Contains(stored.ActorId, new[] { "actor-a", "actor-b" });
        if (!sameText)
        {
            Assert.Equal(stored.Text == leftText ? "actor-a" : "actor-b", stored.ActorId);
        }

        string otherBody = await other.Content.ReadAsStringAsync();
        if (expectedSecondStatus == HttpStatusCode.OK)
        {
            using JsonDocument replayed = JsonDocument.Parse(otherBody);
            AssertSameComment(createdComment, replayed.RootElement);
            Assert.Null(other.Headers.Location);
        }
        else
        {
            using JsonDocument problem = JsonDocument.Parse(otherBody);
            Assert.Equal(conflictCode, problem.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(stored.Text, otherBody, StringComparison.Ordinal);
            Assert.DoesNotContain(stored.ActorId, otherBody, StringComparison.Ordinal);
        }

        if (!sameCase)
        {
            Assert.Equal(1, await CommentCountAsync(organizationId, leftCase) + await CommentCountAsync(organizationId, rightCase));
            Assert.NotEqual(
                await CommentCountAsync(organizationId, leftCase),
                await CommentCountAsync(organizationId, rightCase));
        }
    }

    private async Task<int> CommentCountAsync(OrganizationId organizationId, Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseComments.CountAsync(comment =>
            comment.OrganizationId == organizationId
            && comment.ReviewCaseId == new ReviewCaseId(reviewCaseId));
    }

    private static void AssertSameComment(JsonElement left, JsonElement right)
    {
        Assert.Equal(left.GetProperty("id").GetGuid(), right.GetProperty("id").GetGuid());
        Assert.Equal(left.GetProperty("text").GetString(), right.GetProperty("text").GetString());
        Assert.Equal(left.GetProperty("actorId").GetString(), right.GetProperty("actorId").GetString());
        Assert.Equal(left.GetProperty("recordedAt").GetString(), right.GetProperty("recordedAt").GetString());
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
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

    private sealed record CaseState(
        ReviewCaseStatus Status,
        ReviewCaseOutcome? Outcome,
        DateTimeOffset? ClosedAt,
        string RowVersion);

    /// <summary>
    /// Holds the second initial miss until both SQL reads have returned no row.
    /// Later lookups, including recovery of the committed winner, are not held.
    /// </summary>
    private sealed class CommentCollisionProbe : IReviewCaseCommentConflict
    {
        private readonly ReviewCaseCommentConflict _conflict = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initialMisses;
        private int _namedConflicts;

        public int InitialMisses => Volatile.Read(ref _initialMisses);

        public int NamedConflicts => Volatile.Read(ref _namedConflicts);

        public async Task HoldInitialMissAsync(CancellationToken cancellationToken)
        {
            int seen = Interlocked.Increment(ref _initialMisses);
            if (seen > 2)
            {
                return;
            }

            if (seen == 2)
            {
                _release.TrySetResult();
            }

            await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        public bool IsIdempotencyKeyConflict(Exception exception)
        {
            bool named = _conflict.IsIdempotencyKeyConflict(exception);
            if (named)
            {
                Interlocked.Increment(ref _namedConflicts);
            }

            return named;
        }
    }

    private sealed class BarrierCommentLookup : IReviewCaseCommentLookup
    {
        private readonly ReviewCaseCommentStore _store;
        private readonly CommentCollisionProbe _probe;

        public BarrierCommentLookup(ReviewCaseCommentStore store, CommentCollisionProbe probe)
        {
            _store = store;
            _probe = probe;
        }

        public async Task<ReviewCaseComment?> FindByIdempotencyKeyAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            ReviewCaseComment? found = await _store.FindByIdempotencyKeyAsync(
                organizationId,
                idempotencyKey,
                cancellationToken);
            if (found is null)
            {
                await _probe.HoldInitialMissAsync(cancellationToken);
            }

            return found;
        }
    }
}