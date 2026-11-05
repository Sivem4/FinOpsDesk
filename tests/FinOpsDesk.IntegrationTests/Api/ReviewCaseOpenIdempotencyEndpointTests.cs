using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCaseOpenIdempotencyEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ReviewCaseOpenIdempotencyEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Absent_header_still_reopens_a_subject_after_dismissal()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        using HttpClient client = Client(organizationId);
        Guid first = await OpenAsync(client, invoice.Id.Value, "first", idempotencyKey: null);
        await DismissAsync(client, first);

        Guid second = await OpenAsync(client, invoice.Id.Value, "again", idempotencyKey: null);

        Assert.NotEqual(first, second);
        Assert.Equal(0, await CountAttemptsAsync(organizationId));
    }

    [Fact]
    public async Task Present_empty_key_is_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new
            {
                reason = "NeedsInvestigation",
                invoiceId = invoice.Id.Value,
                sourceLabel = "manual",
                note = "note",
            },
            JsonOptions));
        using HttpClient warmup = Client(organizationId);
        HttpContext httpContext = await _factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/review-cases";
            context.Request.Headers.Authorization = "Bearer " + TestAccessTokens.Create(organizationId.Value);
            context.Request.Headers["Idempotency-Key"] = string.Empty;
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(body);
            context.Request.ContentLength = body.Length;
        });

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        using StreamReader reader = new(httpContext.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using JsonDocument document = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseIdempotencyKeyInvalid.Code,
            document.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, await CountCasesAsync(organizationId));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\u00A0")]
    public async Task Present_whitespace_key_is_rejected(string key)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await PostAsync(client, invoice.Id.Value, "note", key),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseIdempotencyKeyInvalid.Code);

        Assert.Equal(0, await CountCasesAsync(organizationId));
    }

    [Fact]
    public async Task Surrounding_whitespace_is_rejected_when_the_guid_parser_accepts_it()
    {
        string key = " " + Guid.NewGuid().ToString("D") + "\t";
        Assert.True(Guid.TryParseExact(key, "D", out _));
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await PostAsync(client, invoice.Id.Value, "note", key),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseIdempotencyKeyInvalid.Code);
        Assert.Equal(0, await CountCasesAsync(organizationId));
    }

    [Fact]
    public async Task Repeated_invalid_and_empty_guid_values_are_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        using HttpClient client = Client(organizationId);
        string valid = Guid.NewGuid().ToString("D");

        await AssertProblemAsync(
            await PostAsync(client, invoice.Id.Value, "note", valid, valid),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseIdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            await PostAsync(client, invoice.Id.Value, "note", Guid.NewGuid().ToString("N")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseIdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            await PostAsync(client, invoice.Id.Value, "note", Guid.Empty.ToString("D")),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseIdempotencyKeyInvalid.Code);
        Assert.Equal(0, await CountCasesAsync(organizationId));
    }

    [Fact]
    public async Task Same_key_replays_after_resolve_and_dismiss_without_changing_the_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice resolvedInvoice = await SeedInvoiceAsync(organizationId);
        Invoice dismissedInvoice = await SeedInvoiceAsync(organizationId);
        using HttpClient client = Client(organizationId);
        Guid resolvedKey = Guid.NewGuid();
        Guid dismissedKey = Guid.NewGuid();

        HttpResponseMessage resolvedOpen = await PostAsync(
            client,
            resolvedInvoice.Id.Value,
            "resolve me",
            resolvedKey.ToString("D"));
        Assert.Equal(HttpStatusCode.Created, resolvedOpen.StatusCode);
        Guid resolvedId = await ReadIdAsync(resolvedOpen);
        string resolvedToken = await TokenAsync(resolvedId);
        HttpResponseMessage resolved = await PostJsonAsync(
            client,
            $"/api/review-cases/{resolvedId:D}/resolve",
            new { outcome = "NoActionRequired", resolutionNote = "done", concurrencyToken = resolvedToken });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        string tokenAfterResolve = await TokenAsync(resolvedId);

        HttpResponseMessage resolvedReplay = await PostAsync(
            client,
            resolvedInvoice.Id.Value,
            "  resolve me  ",
            resolvedKey.ToString("D"));
        Assert.Equal(HttpStatusCode.OK, resolvedReplay.StatusCode);
        Assert.Null(resolvedReplay.Headers.Location);
        using JsonDocument resolvedDocument = await ReadJsonAsync(resolvedReplay);
        Assert.Equal(resolvedId, resolvedDocument.RootElement.GetProperty("reviewCaseId").GetGuid());
        Assert.Equal("Resolved", resolvedDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal(tokenAfterResolve, resolvedDocument.RootElement.GetProperty("concurrencyToken").GetString());
        Assert.Equal(tokenAfterResolve, await TokenAsync(resolvedId));
        Assert.False(resolvedDocument.RootElement.TryGetProperty("created", out _));

        HttpResponseMessage dismissedOpen = await PostAsync(
            client,
            dismissedInvoice.Id.Value,
            "dismiss me",
            dismissedKey.ToString("D"));
        Guid dismissedId = await ReadIdAsync(dismissedOpen);
        await DismissAsync(client, dismissedId);
        string dismissedToken = await TokenAsync(dismissedId);
        HttpResponseMessage dismissedReplay = await PostAsync(
            client,
            dismissedInvoice.Id.Value,
            "dismiss me",
            dismissedKey.ToString("D"));
        Assert.Equal(HttpStatusCode.OK, dismissedReplay.StatusCode);
        Assert.Null(dismissedReplay.Headers.Location);
        using JsonDocument dismissedDocument = await ReadJsonAsync(dismissedReplay);
        Assert.Equal(dismissedId, dismissedDocument.RootElement.GetProperty("reviewCaseId").GetGuid());
        Assert.Equal("Dismissed", dismissedDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal(dismissedToken, dismissedDocument.RootElement.GetProperty("concurrencyToken").GetString());
        Assert.Equal(1, await CountAuditsAsync(organizationId, resolvedId, ReviewCaseAuditAction.Opened));
        Assert.Equal(1, await CountAuditsAsync(organizationId, dismissedId, ReviewCaseAuditAction.Opened));
        Assert.Equal(2, await CountAttemptsAsync(organizationId));
    }

    [Fact]
    public async Task Same_key_with_a_different_normalized_payload_conflicts()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        using HttpClient client = Client(organizationId);
        string key = Guid.NewGuid().ToString("D");
        Guid created = await ReadIdAsync(await PostAsync(client, invoice.Id.Value, "kept", key));

        await AssertProblemAsync(
            await PostAsync(client, invoice.Id.Value, "\uFEFFkept", key),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseIdempotencyPayloadConflict.Code);
        Assert.Equal(1, await CountCasesAsync(organizationId));
        Assert.Equal(created, await SingleAttemptCaseIdAsync(organizationId, Guid.Parse(key)));
    }

    [Fact]
    public async Task A_different_key_binds_to_the_open_case_and_a_mismatch_does_not()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        using HttpClient client = Client(organizationId);
        Guid firstKey = Guid.NewGuid();
        Guid created = await ReadIdAsync(await PostAsync(client, invoice.Id.Value, "stored", firstKey.ToString("D")));
        Guid secondKey = Guid.NewGuid();

        HttpResponseMessage bound = await PostAsync(client, invoice.Id.Value, "stored", secondKey.ToString("D"));
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
        Assert.Null(bound.Headers.Location);
        Assert.Equal(created, await ReadIdAsync(bound));
        Assert.Equal(created, await SingleAttemptCaseIdAsync(organizationId, secondKey));
        Assert.Equal(1, await CountAuditsAsync(organizationId, created, ReviewCaseAuditAction.Opened));

        await AssertProblemAsync(
            await PostAsync(client, invoice.Id.Value, "other", Guid.NewGuid().ToString("D")),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseOpenRequestConflict.Code);
        Assert.Equal(2, await CountAttemptsAsync(organizationId));
    }

    [Fact]
    public async Task The_same_key_is_isolated_by_organization()
    {
        Guid key = Guid.NewGuid();
        OrganizationId north = OrganizationId.New();
        OrganizationId south = OrganizationId.New();
        Invoice northInvoice = await SeedInvoiceAsync(north);
        Invoice southInvoice = await SeedInvoiceAsync(south);
        using HttpClient northClient = Client(north);
        using HttpClient southClient = Client(south);

        Guid northCase = await ReadIdAsync(await PostAsync(northClient, northInvoice.Id.Value, "north", key.ToString("D")));
        Guid southCase = await ReadIdAsync(await PostAsync(southClient, southInvoice.Id.Value, "south", key.ToString("D")));

        Assert.NotEqual(northCase, southCase);
        Assert.Equal(northCase, await SingleAttemptCaseIdAsync(north, key));
        Assert.Equal(southCase, await SingleAttemptCaseIdAsync(south, key));
    }

    [Fact]
    public async Task Replay_without_an_actor_does_not_reveal_the_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        using HttpClient actor = Client(organizationId);
        string key = Guid.NewGuid().ToString("D");
        Guid created = await ReadIdAsync(await PostAsync(actor, invoice.Id.Value, "kept", key));
        using HttpClient anonymous = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject: null));

        string body = await AssertProblemAsync(
            await PostAsync(anonymous, invoice.Id.Value, "kept", key),
            HttpStatusCode.Forbidden,
            ReconciliationApplicationErrors.ActorRequired.Code);

        Assert.DoesNotContain(created.ToString("D"), body, StringComparison.Ordinal);
        Assert.Equal(1, await CountAttemptsAsync(organizationId));
    }

    [Fact]
    public async Task Parallel_requests_with_one_key_leave_one_case_and_one_attempt()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        string key = Guid.NewGuid().ToString("D");
        using HttpClient first = Client(organizationId);
        using HttpClient second = Client(organizationId);

        HttpResponseMessage[] responses = await Task.WhenAll(
            PostAsync(first, invoice.Id.Value, "parallel", key),
            PostAsync(second, invoice.Id.Value, "parallel", key));

        Guid[] ids =
        [
            await ReadIdAsync(responses[0]),
            await ReadIdAsync(responses[1]),
        ];
        Assert.Equal(ids[0], ids[1]);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await CountCasesAsync(organizationId));
        Assert.Equal(1, await CountAttemptsAsync(organizationId));
        Assert.Equal(1, await CountAuditsAsync(organizationId, ids[0], ReviewCaseAuditAction.Opened));
    }

    [Fact]
    public async Task A_historical_case_does_not_receive_an_invented_attempt()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        ReviewCase historical = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            ReviewCaseSource.Manual,
            "historical",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.ReviewCases.Add(historical);
            await context.SaveChangesAsync();
        }

        using HttpClient client = Client(organizationId);
        await DismissAsync(client, historical.Id.Value);
        Guid created = await ReadIdAsync(await PostAsync(
            client,
            invoice.Id.Value,
            "later",
            Guid.NewGuid().ToString("D")));

        Assert.NotEqual(historical.Id.Value, created);
        Assert.Equal(0, await CountAttemptsForCaseAsync(organizationId, historical.Id.Value));
        Assert.Equal(1, await CountAttemptsForCaseAsync(organizationId, created));
        Assert.Equal(0, await CountAuditsAsync(organizationId, historical.Id.Value, ReviewCaseAuditAction.Opened));
        Assert.Equal(1, await CountAuditsAsync(organizationId, historical.Id.Value, ReviewCaseAuditAction.Dismissed));
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HttpClient Client(OrganizationId organizationId)
    {
        return _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
    }

    private async Task<Invoice> SeedInvoiceAsync(OrganizationId organizationId)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(
            organizationId,
            40m,
            "INV-" + Guid.NewGuid().ToString("N")[..12]);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }

    private async Task<Guid> OpenAsync(
        HttpClient client,
        Guid invoiceId,
        string note,
        string? idempotencyKey)
    {
        HttpResponseMessage response = idempotencyKey is null
            ? await PostAsync(client, invoiceId, note)
            : await PostAsync(client, invoiceId, note, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadIdAsync(response);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid invoiceId,
        string note,
        params string?[] idempotencyKeys)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/review-cases")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        reason = "NeedsInvestigation",
                        invoiceId,
                        sourceLabel = "manual",
                        note,
                    },
                    JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        if (idempotencyKeys.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKeys!);
        }

        return await client.SendAsync(request);
    }

    private async Task DismissAsync(HttpClient client, Guid reviewCaseId)
    {
        HttpResponseMessage response = await PostJsonAsync(
            client,
            $"/api/review-cases/{reviewCaseId:D}/dismiss",
            new { resolutionNote = "closed", concurrencyToken = await TokenAsync(reviewCaseId) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string uri, object body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        return await client.SendAsync(request);
    }

    private async Task<string> TokenAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(
            candidate => candidate.Id == new ReviewCaseId(reviewCaseId));
        return ReviewCaseConcurrencyToken.Encode(
            context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue!);
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        using JsonDocument document = await ReadJsonAsync(response);
        return document.RootElement.GetProperty("reviewCaseId").GetGuid();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            Assert.Fail($"Expected 200 or 201 but got {(int)response.StatusCode}. Body: {json}");
        }

        return JsonDocument.Parse(json);
    }

    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == statusCode,
            $"Expected {(int)statusCode} but got {(int)response.StatusCode}. Body: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        return body;
    }

    private async Task<int> CountCasesAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCases.CountAsync(reviewCase => reviewCase.OrganizationId == organizationId);
    }

    private async Task<int> CountAttemptsAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseOpenAttempts.CountAsync(attempt => attempt.OrganizationId == organizationId);
    }

    private async Task<int> CountAttemptsForCaseAsync(OrganizationId organizationId, Guid reviewCaseId)
    {
        ReviewCaseId id = new(reviewCaseId);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseOpenAttempts.CountAsync(attempt =>
            attempt.OrganizationId == organizationId && attempt.ReviewCaseId == id);
    }

    private async Task<Guid> SingleAttemptCaseIdAsync(OrganizationId organizationId, Guid key)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCaseOpenAttempt attempt = await context.ReviewCaseOpenAttempts.SingleAsync(candidate =>
            candidate.OrganizationId == organizationId && candidate.IdempotencyKey == key);
        return attempt.ReviewCaseId.Value;
    }

    private async Task<int> CountAuditsAsync(
        OrganizationId organizationId,
        Guid reviewCaseId,
        ReviewCaseAuditAction action)
    {
        ReviewCaseId id = new(reviewCaseId);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseAuditEntries.CountAsync(entry =>
            entry.OrganizationId == organizationId
            && entry.ReviewCaseId == id
            && entry.Action == action);
    }
}