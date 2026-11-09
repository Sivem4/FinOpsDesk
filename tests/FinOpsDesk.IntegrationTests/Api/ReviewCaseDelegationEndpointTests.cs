using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Security;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCaseDelegationEndpointTests
{
    private readonly SqlServerFixture _fixture;

    public ReviewCaseDelegationEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Development_jwt_delegates_only_a_listed_recipient_and_replays_after_that_recipient_is_removed()
    {
        OrganizationId organizationId = OrganizationId.New();
        string recipientKey = "DevelopmentDelegation:Organizations:" + organizationId.Value.ToString("D") + ":0";
        using FinOpsDeskApiFactory listed = Factory(new Dictionary<string, string?>
        {
            [recipientKey] = " colleague ",
        });
        using HttpClient owner = listed.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "owner"));
        Invoice invoice = await SeedInvoiceAsync(organizationId);
        Guid reviewCaseId = await OpenAsync(owner, invoice.Id.Value);
        string openToken = await AssignmentTokenAsync(owner, reviewCaseId);
        Guid claimKey = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(owner, ClaimPath(reviewCaseId), ClaimBody(openToken), claimKey)).StatusCode);
        string heldToken = await AssignmentTokenAsync(owner, reviewCaseId);

        HttpResponseMessage recipients = await owner.GetAsync("/api/review-cases/delegation-recipients");
        string recipientBody = await recipients.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, recipients.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(recipientBody))
        {
            JsonElement actorIds = document.RootElement.GetProperty("actorIds");
            Assert.Equal(1, actorIds.GetArrayLength());
            Assert.Equal("colleague", actorIds[0].GetString());
        }

        await AssertProblemAsync(
            await PostAsync(owner, DelegatePath(reviewCaseId), DelegateBody(heldToken, "owner"), Guid.NewGuid()),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentDelegateeUnavailable.Code);
        await AssertProblemAsync(
            await PostAsync(owner, DelegatePath(reviewCaseId), DelegateBody(heldToken, "stranger"), Guid.NewGuid()),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentDelegateeUnavailable.Code);
        Assert.Equal(1, await HistoryCountAsync(organizationId));

        Guid delegateKey = Guid.NewGuid();
        string delegateBody = DelegateBody(heldToken, "colleague");
        HttpResponseMessage created = await PostAsync(owner, DelegatePath(reviewCaseId), delegateBody, delegateKey);
        string createdText = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Null(created.Headers.Location);
        using (JsonDocument document = JsonDocument.Parse(createdText))
        {
            Assert.Equal("delegate", document.RootElement.GetProperty("operation").GetString());
            Assert.Equal("owner", document.RootElement.GetProperty("actorId").GetString());
            Assert.Equal("colleague", document.RootElement.GetProperty("recipientActorId").GetString());
            Assert.Equal("developmentJwt", document.RootElement.GetProperty("actorSource").GetString());
            Assert.False(document.RootElement.TryGetProperty("concurrencyToken", out _));
        }

        await AssertProblemAsync(
            await PostAsync(owner, DelegatePath(reviewCaseId), DelegateBody(heldToken, "colleague"), claimKey),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict.Code);

        using FinOpsDeskApiFactory removed = Factory(new Dictionary<string, string?>());
        using HttpClient replayClient = removed.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "owner"));
        HttpResponseMessage empty = await replayClient.GetAsync("/api/review-cases/delegation-recipients");
        using (JsonDocument document = JsonDocument.Parse(await empty.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            Assert.Equal(0, document.RootElement.GetProperty("actorIds").GetArrayLength());
        }

        HttpResponseMessage replay = await PostAsync(replayClient, DelegatePath(reviewCaseId), delegateBody, delegateKey);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()))
        {
            Assert.Equal("colleague", document.RootElement.GetProperty("recipientActorId").GetString());
            Assert.Equal("owner", document.RootElement.GetProperty("actorId").GetString());
        }

        using HttpClient assignee = removed.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "colleague"));
        string currentToken = await AssignmentTokenAsync(assignee, reviewCaseId);
        await AssertProblemAsync(
            await PostAsync(assignee, DelegatePath(reviewCaseId), DelegateBody(currentToken, "owner"), Guid.NewGuid()),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAssignmentDelegateeUnavailable.Code);
        Assert.Equal(2, await HistoryCountAsync(organizationId));
        Assert.Equal(1, await DelegateCountAsync(organizationId));
        Assert.Equal("colleague", (await ReadAssigneeAsync(reviewCaseId)).ActorId);
    }

    private FinOpsDeskApiFactory Factory(IReadOnlyDictionary<string, string?> configuration)
    {
        return new FinOpsDeskApiFactory(
            _fixture.ConnectionString,
            authenticationComposition: AuthenticationCompositions.DevelopmentJwt,
            configurationOverrides: configuration);
    }

    private async Task<Invoice> SeedInvoiceAsync(OrganizationId organizationId)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 25m, "INV-" + Guid.NewGuid().ToString("N")[..8]);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }

    private async Task<int> HistoryCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseAssignmentEntries.CountAsync(entry => entry.OrganizationId == organizationId);
    }

    private async Task<int> DelegateCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseAssignmentEntries.CountAsync(entry =>
            entry.OrganizationId == organizationId
            && entry.Operation == ReviewCaseAssignmentOperation.Delegate);
    }

    private async Task<AssigneeRow> ReadAssigneeAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        var row = await context.ReviewCases.AsNoTracking()
            .Where(reviewCase => reviewCase.Id == new ReviewCaseId(reviewCaseId))
            .Select(reviewCase => new { reviewCase.AssignedActorSource, reviewCase.AssignedActorId })
            .SingleAsync();
        return new AssigneeRow(row.AssignedActorSource, row.AssignedActorId);
    }

    private static async Task<Guid> OpenAsync(HttpClient client, Guid invoiceId)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/review-cases")
        {
            Content = new StringContent(
                "{\"reason\":\"NeedsInvestigation\",\"invoiceId\":\"" + invoiceId.ToString("D") + "\"}",
                Encoding.UTF8,
                "application/json"),
        };
        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reviewCaseId").GetGuid();
    }

    private static async Task<string> AssignmentTokenAsync(HttpClient client, Guid reviewCaseId)
    {
        HttpResponseMessage response = await client.GetAsync(ClaimPath(reviewCaseId));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("concurrencyToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string json, Guid key)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return await client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, body);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    private static string ClaimPath(Guid reviewCaseId)
    {
        return "/api/review-cases/" + reviewCaseId.ToString("D") + "/assignment";
    }

    private static string DelegatePath(Guid reviewCaseId)
    {
        return ClaimPath(reviewCaseId) + "/delegate";
    }

    private static string ClaimBody(string token)
    {
        return "{\"concurrencyToken\":" + JsonSerializer.Serialize(token) + "}";
    }

    private static string DelegateBody(string token, string recipient)
    {
        return "{\"concurrencyToken\":" + JsonSerializer.Serialize(token)
            + ",\"recipientActorId\":" + JsonSerializer.Serialize(recipient) + "}";
    }

    private sealed record AssigneeRow(ReviewCaseActorSource? Source, string? ActorId);
}

public sealed class ReviewCaseDelegationDirectoryTests
{
    [Fact]
    public void Production_cookie_does_not_read_the_delegation_section()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guard configuration = new(new Dictionary<string, string?>
        {
            [AuthenticationCompositions.ConfigurationKey] = AuthenticationCompositions.DevelopmentJwt,
            ["DevelopmentDelegation:Organizations:" + organizationId.Value.ToString("D") + ":0"] = "secret-recipient",
        });
        ConfigurationReviewCaseDelegationDirectory directory = new(
            configuration,
            new TestCurrentOrganizationContext(organizationId),
            CookieComposition());

        Assert.IsType<ReviewCaseDelegationDirectoryRead.Unavailable>(directory.ReadCurrentOrganization());
        Assert.False(directory.Allows(organizationId, "secret-recipient"));
        Assert.Equal(0, configuration.DelegationSectionReads);
    }

    [Fact]
    public void Development_jwt_reads_the_organization_list_after_the_composition_check()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guard configuration = new(new Dictionary<string, string?>
        {
            [AuthenticationCompositions.ConfigurationKey] = AuthenticationCompositions.ProductionCookie,
            ["DevelopmentDelegation:Organizations:" + organizationId.Value.ToString("D") + ":0"] = " colleague ",
            ["DevelopmentDelegation:Organizations:" + organizationId.Value.ToString("D") + ":1"] = "colleague",
        });
        ConfigurationReviewCaseDelegationDirectory directory = new(
            configuration,
            new TestCurrentOrganizationContext(organizationId),
            JwtComposition());

        ReviewCaseDelegationDirectoryRead.Available available = Assert.IsType<ReviewCaseDelegationDirectoryRead.Available>(
            directory.ReadCurrentOrganization());
        Assert.Equal(["colleague"], available.ActorIds);
        Assert.True(directory.Allows(organizationId, "colleague"));
        Assert.True(configuration.DelegationSectionReads > 0);
    }

    private static AuthenticationCompositionState CookieComposition()
    {
        return new AuthenticationCompositionState
        {
            UsesProductionCookie = true,
            ProductionFinancialAccessFailClosed = true,
            ProductionFinancialWritesEnabled = false,
        };
    }

    private static AuthenticationCompositionState JwtComposition()
    {
        return new AuthenticationCompositionState
        {
            UsesProductionCookie = false,
            ProductionFinancialAccessFailClosed = false,
            ProductionFinancialWritesEnabled = false,
        };
    }

    private sealed class Guard : IConfiguration
    {
        private readonly IConfiguration _inner;

        public Guard(IReadOnlyDictionary<string, string?> values)
        {
            _inner = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        public int DelegationSectionReads { get; private set; }

        public string? this[string key]
        {
            get => _inner[key];
            set => _inner[key] = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren()
        {
            return _inner.GetChildren();
        }

        public IChangeToken GetReloadToken()
        {
            return _inner.GetReloadToken();
        }

        public IConfigurationSection GetSection(string key)
        {
            if (string.Equals(key, ConfigurationReviewCaseDelegationDirectory.SectionName, StringComparison.Ordinal))
            {
                DelegationSectionReads++;
            }

            return _inner.GetSection(key);
        }
    }
}