using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Authorization;
using FinOpsDesk.Api.Observability;
using FinOpsDesk.Api.RateLimiting;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class OrganizationRateLimitTests
{
    private const string InvoiceCsv =
        "ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV/1,10.00,PLN\n";

    private const string BankCsv =
        "ExternalId,BookingDate,Amount,Currency,Reference\nTXN-1,2026-08-24,1.00,PLN,\n";

    private readonly SqlServerFixture _fixture;

    public OrganizationRateLimitTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Named_policies_are_shared_isolated_and_ignore_client_organization()
    {
        Guid organizationA = Guid.NewGuid();
        Guid organizationB = Guid.NewGuid();
        using FinOpsDeskApiFactory factory = CreateJwt(StableBudgets());
        using HttpClient clientA = factory.CreateApiClient(TestAccessTokens.Create(organizationA));
        using HttpClient clientB = factory.CreateApiClient(TestAccessTokens.Create(organizationB));

        Assert.Equal(HttpStatusCode.OK, await GetAsync(clientA, "/api/matches"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAsync(clientA, "/api/matches/smart"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAsync(clientA, "/api/matches/batch/preview"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(clientB, "/api/matches/batch/preview"));

        using HttpRequestMessage disguised = new(HttpMethod.Get, "/api/matches?organizationId=" + organizationB.ToString("D"));
        disguised.Headers.TryAddWithoutValidation("X-Organization-Id", organizationB.ToString("D"));
        HttpResponseMessage disguisedResponse = await clientA.SendAsync(disguised);
        Assert.Equal(HttpStatusCode.TooManyRequests, disguisedResponse.StatusCode);
        disguisedResponse.Dispose();

        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(clientA, "/api/matches/batch/execute", "{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(clientA, "/api/matches/batch/execute", "{}"));
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(clientB, "/api/matches/batch/execute", "{}"));

        Assert.Equal(HttpStatusCode.Created, await ImportAsync(clientA, "/api/imports/invoices", InvoiceCsv));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ImportAsync(clientA, "/api/imports/bank-transactions", BankCsv));
        Assert.Equal(1, await CountAsync("ImportBatches", organizationA));
        Assert.Equal(HttpStatusCode.Created, await ImportAsync(clientB, "/api/imports/bank-transactions", BankCsv));
        Assert.Equal(1, await CountAsync("ImportBatches", organizationB));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(clientA, "/api/matches/confirm", "{}"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(clientA, "/api/matches/confirm", "{}"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(clientA, "/health"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(clientA, "/ready"));
    }

    [Fact]
    public async Task Fixed_window_and_token_bucket_renew()
    {
        using FinOpsDeskApiFactory reads = CreateJwt(new Dictionary<string, string?>
        {
            ["RateLimiting:MatchingReadsPermitLimit"] = "1",
            ["RateLimiting:MatchingReadsWindowSeconds"] = "1",
        });
        using HttpClient readClient = reads.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));
        await WaitForRenewalAsync(() => GetAsync(readClient, "/api/matches"));

        using FinOpsDeskApiFactory imports = CreateJwt(new Dictionary<string, string?>
        {
            ["RateLimiting:ImportsTokenLimit"] = "1",
            ["RateLimiting:ImportsTokensPerPeriod"] = "1",
            ["RateLimiting:ImportsReplenishmentSeconds"] = "1",
        });
        using HttpClient importClient = imports.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));
        await WaitForRenewalAsync(
            () => ImportAsync(importClient, "/api/imports/invoices", InvoiceCsv),
            HttpStatusCode.Created);
    }

    [Fact]
    public async Task Cookie_partition_follows_the_active_organization()
    {
        await using OidcProtocolStub stub = await OidcProtocolStub.StartAsync();
        using FinOpsDeskApiFactory factory = CreateCookie(stub, readEnabled: true, writeEnabled: false, StableBudgets());
        using HttpClient client = factory.CreateBrowserClient();
        await SignInAsync(client);
        ApplicationUser user = await RequireUserAsync(stub);
        Guid organizationA = await CreateOrganizationAsync();
        Guid organizationB = await CreateOrganizationAsync();
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationA));
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, organizationB));
        Assert.Equal(
            OrganizationRoleMutationStatus.Granted,
            await StoreGrantAsync(await MembershipIdAsync(user.Id, organizationA), OrganizationRole.Viewer));
        Assert.Equal(
            OrganizationRoleMutationStatus.Granted,
            await StoreGrantAsync(await MembershipIdAsync(user.Id, organizationB), OrganizationRole.Viewer));
        string csrf = await ReadCsrfAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/api/matches"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAsync(client, "/api/matches/smart"));
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationB)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/api/matches"));
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(client, csrf, organizationA)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAsync(client, "/api/matches/batch/preview"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/api/session"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/health"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/ready"));
    }

    [Fact]
    public async Task Authorization_and_csrf_win_and_do_not_select_a_partition()
    {
        Guid organizationId = Guid.NewGuid();
        SelectorCounter jwtCounter = new();
        using FinOpsDeskApiFactory jwt = CreateJwt(StableBudgets(), services => Observe(services, jwtCounter));
        using HttpClient allowed = jwt.CreateApiClient(TestAccessTokens.Create(organizationId));
        using HttpClient anonymous = jwt.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        Assert.Equal(HttpStatusCode.OK, await GetAsync(allowed, "/api/matches"));
        int selected = jwtCounter.Count;
        Assert.True(selected >= 1);
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(anonymous, "/api/matches"));
        Assert.Equal(selected, jwtCounter.Count);

        await using OidcProtocolStub stub = await OidcProtocolStub.StartAsync();
        SelectorCounter cookieCounter = new();
        using FinOpsDeskApiFactory cookie = CreateCookie(
            stub,
            readEnabled: true,
            writeEnabled: true,
            StableBudgets(),
            services => Observe(services, cookieCounter));
        using HttpClient browser = cookie.CreateBrowserClient();
        await SignInAsync(browser);
        ApplicationUser user = await RequireUserAsync(stub);
        Guid active = await CreateOrganizationAsync();
        Assert.Equal(DeploymentCommandStatus.MembershipGranted, await GrantMembershipAsync(user, active));
        Guid membershipId = await MembershipIdAsync(user.Id, active);
        string csrf = await ReadCsrfAsync(browser);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(browser, csrf, active)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(browser, "/api/matches"));
        Assert.Equal(0, cookieCounter.Count);

        Assert.Equal(OrganizationRoleMutationStatus.Granted, await StoreGrantAsync(membershipId, OrganizationRole.Operator));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(browser, "/api/matches"));
        int afterRead = cookieCounter.Count;
        Assert.Equal(HttpStatusCode.Forbidden, await ImportAsync(browser, "/api/imports/invoices", InvoiceCsv, csrf));
        Assert.Equal(HttpStatusCode.Forbidden, await ImportAsync(browser, "/api/imports/bank-transactions", BankCsv, csrf));
        Assert.Equal(HttpStatusCode.Forbidden, await ExecuteAsync(browser, Guid.NewGuid(), Guid.NewGuid(), csrf));
        Assert.Equal(afterRead, cookieCounter.Count);
        Assert.Equal(0, await CountAsync("ImportBatches", active));
        Assert.Equal(0, await CountAsync("Allocations", active));
        Assert.Equal(0, await CountAsync("BatchExecutions", active));

        RequestReadCounter reads = new();
        SelectorCounter csrfCounter = new();
        using FinOpsDeskApiFactory csrfFactory = CreateCookie(
            stub,
            readEnabled: true,
            writeEnabled: false,
            StableBudgets(),
            services =>
            {
                services.AddSingleton<IAuthorizationHandler, SucceedAllRequirementsHandler>();
                services.AddSingleton(reads);
                services.AddSingleton<IStartupFilter, CountingBodyStartupFilter>();
                UseClosedImports(services, csrfCounter);
            });
        using HttpClient csrfClient = csrfFactory.CreateBrowserClient();
        await SignInAsync(csrfClient);
        string csrfToken = await ReadCsrfAsync(csrfClient);
        Assert.Equal(HttpStatusCode.NoContent, (await SelectAsync(csrfClient, csrfToken, active)).StatusCode);
        reads.Reset();
        Assert.Equal(HttpStatusCode.TooManyRequests, await ImportAsync(csrfClient, "/api/imports/invoices", InvoiceCsv, csrfToken));
        int afterRejection = csrfCounter.Count;
        Assert.Equal(0, reads.Count);
        reads.Reset();
        HttpResponseMessage missing = await ImportAsyncResponse(csrfClient, "/api/imports/invoices", InvoiceCsv, csrf: null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("auth.csrf.invalid", await ProblemCodeAsync(missing));
        Assert.Equal(afterRejection, csrfCounter.Count);
        Assert.Equal(0, reads.Count);
        Assert.Equal(0, await CountAsync("ImportBatches", active));

        SelectorCounter gapCounter = new();
        using FinOpsDeskApiFactory gap = CreateJwt(
            null,
            services =>
            {
                services.AddSingleton<IAuthorizationHandler, SucceedAllRequirementsHandler>();
                Observe(services, gapCounter);
            });
        using HttpClient gapClient = gap.CreateApiClient(TestAccessTokens.Create(organizationId));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(gapClient, "/api/matches/smart"));
        int afterPartition = gapCounter.Count;
        using HttpClient missingOrganization = gap.CreateApiClient(TestAccessTokens.CreateWithoutOrganizationClaim());
        using HttpRequestMessage gapRequest = new(HttpMethod.Get, "/api/matches");
        HttpResponseMessage gapResponse = await missingOrganization.SendAsync(gapRequest);
        Assert.Equal(HttpStatusCode.Forbidden, gapResponse.StatusCode);
        AssertHeaderContains(gapResponse, "Cache-Control", "no-store");
        string gapBody = await gapResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(RateLimitRejection.Code, gapBody, StringComparison.Ordinal);
        Assert.Equal(afterPartition, gapCounter.Count);

        using FinOpsDeskApiFactory probes = CreateJwt(
            StableBudgets(),
            probesEnabled: true);
        using HttpClient probeClient = probes.CreateApiClient(TestAccessTokens.Create(organizationId));
        foreach (string path in new[]
        {
            "/api/test/financial-unclassified",
            "/api/test/financial-unknown",
            "/api/test/financial-write-permission",
            "/api/test/financial-write-on-get",
            "/api/test/financial-allow-anonymous",
        })
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await GetAsync(probeClient, path));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await GetAsync(probeClient, path));
        }

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(probeClient, "/api/test/financial-read-on-post", "{}"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(probeClient, "/api/test/financial-write-unlisted", "{}"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(browser, "/api/session"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(browser, "/login?returnUrl=/", "{}"));
    }

    [Fact]
    public async Task Rejection_is_problem_json_before_the_handler_and_form_read()
    {
        Guid organizationId = Guid.NewGuid();
        RequestLogCollector collector = new();
        RequestReadCounter reads = new();
        CountingHandlers handlers = new();
        using FinOpsDeskApiFactory factory = CreateJwt(
            null,
            services =>
            {
                services.AddSingleton<ILoggerProvider>(collector);
                services.AddSingleton(reads);
                services.AddSingleton<IStartupFilter, CountingBodyStartupFilter>();
                services.Replace(ServiceDescriptor.Singleton<IFinancialHandlerObservation>(handlers));
                UseScriptedReads(services);
            });
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId));
        collector.Clear();
        using HttpRequestMessage plain = new(HttpMethod.Get, "/api/matches");
        plain.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        HttpResponseMessage first = await client.SendAsync(plain);
        string body = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.StartsWith("application/problem+json", first.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        AssertHeaderContains(first, "Cache-Control", "no-store");
        Assert.Equal("1", Header(first, "Retry-After"));
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(RateLimitRejection.Title, document.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(RateLimitRejection.Code, document.RootElement.GetProperty("code").GetString());
        string requestId = Header(first, RequestLog.HeaderName);
        Assert.Equal(requestId, document.RootElement.GetProperty("traceId").GetString());
        RequestLogEntry entry = Assert.Single(collector.Entries, candidate => candidate.EventId == RequestLog.Completed.Id);
        Assert.Equal(requestId, entry.State["requestId"]?.ToString());
        Assert.Equal(requestId, entry.State["problemTraceId"]?.ToString());
        Assert.DoesNotContain(organizationId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OrganizationRateLimitPolicies.MatchingReads, body, StringComparison.Ordinal);
        Assert.DoesNotContain(organizationId.ToString(), entry.Message, StringComparison.OrdinalIgnoreCase);

        HttpResponseMessage rounded = await client.GetAsync("/api/matches/smart");
        Assert.Equal("3", Header(rounded, "Retry-After"));
        rounded.Dispose();
        HttpResponseMessage zero = await client.GetAsync("/api/matches/batch/preview");
        Assert.False(zero.Headers.Contains("Retry-After"));
        zero.Dispose();
        HttpResponseMessage negative = await client.GetAsync("/api/matches");
        Assert.False(negative.Headers.Contains("Retry-After"));
        negative.Dispose();
        HttpResponseMessage absent = await client.GetAsync("/api/matches/smart");
        Assert.False(absent.Headers.Contains("Retry-After"));
        absent.Dispose();

        reads.Reset();
        int importsBefore = handlers.Imports;
        using FinOpsDeskApiFactory closed = CreateJwt(
            StableBudgets(),
            services =>
            {
                services.AddSingleton(reads);
                services.AddSingleton<IStartupFilter, CountingBodyStartupFilter>();
                services.Replace(ServiceDescriptor.Singleton<IFinancialHandlerObservation>(handlers));
            });
        using HttpClient writer = closed.CreateApiClient(TestAccessTokens.Create(organizationId));
        Assert.Equal(HttpStatusCode.Created, await ImportAsync(writer, "/api/imports/invoices", InvoiceCsv));
        reads.Reset();
        int batches = await CountAsync("ImportBatches", organizationId);
        HttpResponseMessage rejected = await ImportAsyncResponse(writer, "/api/imports/invoices", InvoiceCsv, csrf: null, fileName: "secret-file.csv");
        string rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(0, reads.Count);
        Assert.Equal(importsBefore + 1, handlers.Imports);
        Assert.Equal(batches, await CountAsync("ImportBatches", organizationId));
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(writer, "/api/matches/batch/execute", "{}"));
        int executesAfterPermitted = handlers.Executes;
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(writer, "/api/matches/batch/execute", "{}"));
        Assert.Equal(executesAfterPermitted, handlers.Executes);
        Assert.DoesNotContain("secret-file.csv", rejectedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(organizationId.ToString(), rejectedBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lost_import_response_retries_the_same_batch_after_the_permit_returns()
    {
        Guid organizationId = Guid.NewGuid();
        TestPermitGate gate = new();
        gate.Open(organizationId);
        using FinOpsDeskApiFactory factory = CreateJwt(null, services => AddGate(services, gate));
        using HttpClient held = factory.CreateApiClient(TestAccessTokens.Create(organizationId));
        using HttpClient later = factory.CreateApiClient(TestAccessTokens.Create(organizationId));
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        using HttpRequestMessage first = ImportRequest("/api/imports/invoices", InvoiceCsv, csrf: null);
        first.Headers.TryAddWithoutValidation(HoldResponseStartupFilter.HeaderName, "1");
        Task<HttpResponseMessage> pending = held.SendAsync(first, cancellation.Token);
        await gate.EndpointCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await CountAsync("ImportBatches", organizationId));
        Guid batchId = await SingleIdAsync("ImportBatches", organizationId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);

        gate.Close(organizationId);
        HttpResponseMessage rejected = await ImportAsyncResponse(later, "/api/imports/invoices", InvoiceCsv, csrf: null);
        string rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(RateLimitRejection.Code, await ProblemCodeAsync(rejected));
        Assert.DoesNotContain(batchId.ToString(), rejectedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await CountAsync("ImportBatches", organizationId));

        gate.Open(organizationId);
        HttpResponseMessage replayed = await ImportAsyncResponse(later, "/api/imports/invoices", InvoiceCsv, csrf: null);
        string replayedBody = await replayed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        using JsonDocument document = JsonDocument.Parse(replayedBody);
        Assert.True(document.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(batchId, document.RootElement.GetProperty("importBatchId").GetGuid());
        Assert.Equal(1, await CountAsync("ImportBatches", organizationId));
        AssertSamePartition(gate, OrganizationRateLimitPolicies.Imports, organizationId);
    }

    [Fact]
    public async Task Lost_execute_response_retries_the_same_allocations_after_the_permit_returns()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "HOLD-1");
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, 10m, reference: "HOLD-1");
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.AddRange(invoice, bank);
            await context.SaveChangesAsync();
        }

        TestPermitGate gate = new();
        gate.Open(organizationId.Value);
        Guid key = Guid.NewGuid();
        CountingHandlers handlers = new();
        using FinOpsDeskApiFactory factory = CreateJwt(
            null,
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IFinancialHandlerObservation>(handlers));
                AddGate(services, gate);
            });
        using HttpClient held = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using HttpClient later = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        using HttpRequestMessage first = ExecuteRequest(invoice.Id.Value, bank.Id.Value, key, hold: true);
        Task<HttpResponseMessage> pending = held.SendAsync(first, cancellation.Token);
        await gate.EndpointCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await CountAsync("Allocations", organizationId.Value));
        Guid allocationId = await SingleIdAsync("Allocations", organizationId.Value);
        (int status, int resultVersion, string payload) = await ReadBatchExecutionAsync(organizationId.Value);
        Assert.Equal(2, status);
        Assert.Equal(1, resultVersion);
        Assert.Contains(allocationId.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
        int executesAfterCommit = handlers.Executes;
        Assert.True(executesAfterCommit >= 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);

        gate.Close(organizationId.Value);
        HttpResponseMessage rejected = await later.SendAsync(ExecuteRequest(invoice.Id.Value, bank.Id.Value, key, hold: false));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(RateLimitRejection.Code, await ProblemCodeAsync(rejected));
        Assert.Equal(executesAfterCommit, handlers.Executes);
        Assert.Equal(1, await CountAsync("Allocations", organizationId.Value));
        Assert.Equal(1, await CountAsync("BatchExecutions", organizationId.Value));
        rejected.Dispose();

        gate.Open(organizationId.Value);
        HttpResponseMessage replayed = await later.SendAsync(ExecuteRequest(invoice.Id.Value, bank.Id.Value, key, hold: false));
        string body = await replayed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(allocationId, item.GetProperty("allocationId").GetGuid());
        Assert.Equal(1, await CountAsync("Allocations", organizationId.Value));
        Assert.Equal(1, await CountAsync("BatchExecutions", organizationId.Value));
        AssertSamePartition(gate, OrganizationRateLimitPolicies.MatchingExecute, organizationId.Value);
    }

    [Fact]
    public async Task OpenApi_documents_the_six_limited_operations()
    {
        using FinOpsDeskApiFactory factory = CreateJwt(null);
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        Dictionary<string, string> limited = [];
        foreach (Endpoint endpoint in dataSource.Endpoints)
        {
            EnableRateLimitingAttribute? policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
            if (policy is null || endpoint is not RouteEndpoint route)
            {
                continue;
            }

            string? method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.SingleOrDefault();
            limited[(method ?? string.Empty) + " " + route.RoutePattern.RawText] = policy.PolicyName ?? string.Empty;
        }

        Assert.Equal(6, limited.Count);
        Assert.Equal(OrganizationRateLimitPolicies.MatchingReads, Policy(limited, "GET", "/api/matches"));
        Assert.Equal(OrganizationRateLimitPolicies.MatchingReads, Policy(limited, "GET", "/api/matches/smart"));
        Assert.Equal(OrganizationRateLimitPolicies.MatchingReads, Policy(limited, "GET", "/api/matches/batch/preview"));
        Assert.Equal(OrganizationRateLimitPolicies.MatchingExecute, Policy(limited, "POST", "/api/matches/batch/execute"));
        Assert.Equal(OrganizationRateLimitPolicies.Imports, Policy(limited, "POST", "/api/imports/invoices"));
        Assert.Equal(OrganizationRateLimitPolicies.Imports, Policy(limited, "POST", "/api/imports/bank-transactions"));

        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));
        using JsonDocument document = JsonDocument.Parse(await (await client.GetAsync("/openapi/v1.json")).Content.ReadAsStringAsync());
        foreach ((string method, string path) in new[]
        {
            ("get", "/api/matches"),
            ("get", "/api/matches/smart"),
            ("get", "/api/matches/batch/preview"),
            ("post", "/api/matches/batch/execute"),
            ("post", "/api/imports/invoices"),
            ("post", "/api/imports/bank-transactions"),
        })
        {
            JsonElement response = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method)
                .GetProperty("responses").GetProperty("429");
            string? description = response.GetProperty("description").GetString();
            Assert.Contains(RateLimitRejection.Code, description, StringComparison.Ordinal);
            Assert.Contains("Default budget", description, StringComparison.Ordinal);
            Assert.Contains("captured at startup", description, StringComparison.Ordinal);
            Assert.True(response.GetProperty("headers").TryGetProperty("Retry-After", out JsonElement retry));
            Assert.False(retry.TryGetProperty("required", out JsonElement required) && required.GetBoolean());
        }

        Assert.False(document.RootElement.GetProperty("paths").GetProperty("/api/matches/confirm")
            .GetProperty("post").GetProperty("responses").TryGetProperty("429", out _));
    }

    [Fact]
    public void Invalid_budget_fails_before_requests()
    {
        using FinOpsDeskApiFactory factory = CreateJwt(new Dictionary<string, string?>
        {
            ["RateLimiting:MatchingReadsPermitLimit"] = "0",
        });
        Exception thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("RateLimiting:MatchingReadsPermitLimit", thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reloaded_budget_keeps_the_startup_snapshot()
    {
        using FinOpsDeskApiFactory factory = CreateJwt(new Dictionary<string, string?>
        {
            ["RateLimiting:MatchingReadsPermitLimit"] = "1",
            ["RateLimiting:MatchingReadsWindowSeconds"] = "3600",
        });
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));
        IConfigurationRoot root = Assert.IsAssignableFrom<IConfigurationRoot>(
            factory.Services.GetRequiredService<IConfiguration>());
        const string permitKey = "RateLimiting:MatchingReadsPermitLimit";
        IConfigurationProvider? active = null;
        foreach (IConfigurationProvider provider in root.Providers)
        {
            if (provider.TryGet(permitKey, out _))
            {
                active = provider;
            }
        }

        Assert.NotNull(active);
        active.Set(permitKey, "100");
        root.Reload();
        Assert.Equal("100", root[permitKey]);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/api/matches"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAsync(client, "/api/matches/smart"));
    }

    private FinOpsDeskApiFactory CreateJwt(
        IReadOnlyDictionary<string, string?>? overrides,
        Action<IServiceCollection>? configure = null,
        bool probesEnabled = false)
    {
        return new FinOpsDeskApiFactory(
            _fixture.ConnectionString,
            configure,
            configurationOverrides: overrides,
            financialClassificationProbesEnabled: probesEnabled);
    }

    private FinOpsDeskApiFactory CreateCookie(
        OidcProtocolStub stub,
        bool readEnabled,
        bool writeEnabled,
        IReadOnlyDictionary<string, string?> budgets,
        Action<IServiceCollection>? configure = null)
    {
        Dictionary<string, string?> settings = new(budgets)
        {
            ["Authentication:Oidc:Authority"] = stub.Authority,
            ["Authentication:Oidc:ClientId"] = OidcProtocolStub.ClientId,
            ["Authentication:Oidc:ClientSecret"] = OidcProtocolStub.ClientSecret,
            ["Authentication:Oidc:BrowserOrigin"] = "https://localhost",
            ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
        };
        return new FinOpsDeskApiFactory(
            _fixture.ConnectionString,
            configure,
            environmentName: "Production",
            authenticationComposition: AuthenticationCompositions.ProductionCookie,
            configurationOverrides: settings,
            financialReadAuthorizationEnabled: readEnabled,
            financialWriteAuthorizationEnabled: writeEnabled);
    }

    private static Dictionary<string, string?> StableBudgets()
    {
        return new Dictionary<string, string?>
        {
            ["RateLimiting:MatchingReadsPermitLimit"] = "1",
            ["RateLimiting:MatchingReadsWindowSeconds"] = "3600",
            ["RateLimiting:MatchingExecutePermitLimit"] = "1",
            ["RateLimiting:MatchingExecuteWindowSeconds"] = "3600",
            ["RateLimiting:ImportsTokenLimit"] = "1",
            ["RateLimiting:ImportsTokensPerPeriod"] = "1",
            ["RateLimiting:ImportsReplenishmentSeconds"] = "3600",
        };
    }

    private static void Observe(IServiceCollection services, SelectorCounter counter)
    {
        services.AddSingleton(counter);
        services.RemoveAll<IOrganizationRateLimitPartitioner>();
        services.AddSingleton<IOrganizationRateLimitPartitioner>(provider =>
            new ObservingPartitioner(
                ActivatorUtilities.CreateInstance<ConfiguredOrganizationRateLimitPartitioner>(provider),
                counter));
    }

    private static void UseScriptedReads(IServiceCollection services)
    {
        services.RemoveAll<IOrganizationRateLimitPartitioner>();
        services.AddSingleton<IOrganizationRateLimitPartitioner, ScriptedReadPartitioner>();
    }

    private static void UseClosedImports(IServiceCollection services, SelectorCounter counter)
    {
        services.AddSingleton(counter);
        services.RemoveAll<IOrganizationRateLimitPartitioner>();
        services.AddSingleton<IOrganizationRateLimitPartitioner>(provider =>
            new ClosedImportsPartitioner(
                ActivatorUtilities.CreateInstance<ConfiguredOrganizationRateLimitPartitioner>(provider),
                counter));
    }

    private static void AddGate(IServiceCollection services, TestPermitGate gate)
    {
        services.AddSingleton(gate);
        services.AddSingleton<IStartupFilter, HoldResponseStartupFilter>();
        services.RemoveAll<IOrganizationRateLimitPartitioner>();
        services.AddSingleton<IOrganizationRateLimitPartitioner, GatePartitioner>();
    }

    private static void AssertSamePartition(TestPermitGate gate, string policy, Guid organizationId)
    {
        string expected = OrganizationRateLimitPolicies.PartitionKey(policy, organizationId);
        string[] keys = gate.Keys.ToArray();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Equal(expected, key));
        Assert.Single(keys.Distinct());
    }

    private async Task<int> CountAsync(string table, Guid organizationId)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            "SELECT COUNT(*) FROM [reconciliation].[" + table + "] WHERE OrganizationId = @organizationId",
            connection);
        command.Parameters.Add(new SqlParameter("@organizationId", organizationId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<Guid> SingleIdAsync(string table, Guid organizationId)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            "SELECT Id FROM [reconciliation].[" + table + "] WHERE OrganizationId = @organizationId",
            connection);
        command.Parameters.Add(new SqlParameter("@organizationId", organizationId));
        object? value = await command.ExecuteScalarAsync();
        return Assert.IsType<Guid>(value);
    }

    private async Task<(int Status, int ResultVersion, string Payload)> ReadBatchExecutionAsync(Guid organizationId)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            "SELECT Status, ResultVersion, ResultPayload FROM [reconciliation].[BatchExecutions] WHERE OrganizationId = @organizationId",
            connection);
        command.Parameters.Add(new SqlParameter("@organizationId", organizationId));
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        int status = reader.GetInt32(0);
        int resultVersion = reader.GetInt32(1);
        string payload = reader.GetString(2);
        Assert.False(await reader.ReadAsync());
        return (status, resultVersion, payload);
    }

    private async Task<ApplicationUser> RequireUserAsync(OidcProtocolStub stub)
    {
        byte[] identityKey = ExternalIdentityKey.Compute(stub.Authority, OidcProtocolStub.Subject);
        await using AccessDbContext context = _fixture.CreateAccessContext();
        ApplicationUser? user = (await context.ApplicationUsers.AsNoTracking()
            .Where(candidate => candidate.IdentityKey == identityKey)
            .ToListAsync())
            .SingleOrDefault(candidate => ExternalIdentityKey.Same(
                candidate.Issuer,
                candidate.Subject,
                stub.Authority,
                OidcProtocolStub.Subject));
        Assert.NotNull(user);
        return user;
    }

    private async Task<Guid> CreateOrganizationAsync()
    {
        Guid organizationId = Guid.NewGuid();
        await using AccessDbContext context = _fixture.CreateAccessContext();
        DeploymentRoleBootstrap bootstrap = new(context, new OrganizationRoleStore(context), TimeProvider.System);
        Assert.Equal(
            DeploymentCommandStatus.OrganizationCreated,
            await bootstrap.CreateOrganizationAsync(organizationId, "Ledger " + organizationId.ToString("N"), CancellationToken.None));
        return organizationId;
    }

    private async Task<DeploymentCommandStatus> GrantMembershipAsync(ApplicationUser user, Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        DeploymentRoleBootstrap bootstrap = new(context, new OrganizationRoleStore(context), TimeProvider.System);
        return await bootstrap.GrantMembershipAsync(user.Issuer, user.Subject, organizationId, CancellationToken.None);
    }

    private async Task<OrganizationRoleMutationStatus> StoreGrantAsync(Guid membershipId, OrganizationRole role)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await new OrganizationRoleStore(context).GrantAsync(
            membershipId,
            role,
            DateTimeOffset.UtcNow,
            RoleActor.DeploymentBootstrap("deploy-operator"),
            CancellationToken.None);
    }

    private async Task<Guid> MembershipIdAsync(Guid userId, Guid organizationId)
    {
        await using AccessDbContext context = _fixture.CreateAccessContext();
        return await context.OrganizationMemberships
            .Where(membership => membership.UserId == userId && membership.OrganizationId == organizationId)
            .Select(membership => membership.Id)
            .SingleAsync();
    }

    private static async Task SignInAsync(HttpClient client)
    {
        string csrf = await ReadCsrfAsync(client);
        using HttpRequestMessage login = new(HttpMethod.Post, "/login?returnUrl=/");
        login.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        HttpResponseMessage loginResponse = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        using HttpClient provider = new(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage authorize = await provider.GetAsync(loginResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        HttpResponseMessage callback = await client.GetAsync(authorize.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        callback.Dispose();
        loginResponse.Dispose();
    }

    private static async Task<string> ReadCsrfAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("csrfToken").GetString()
            ?? throw new InvalidOperationException("CSRF token was missing.");
    }

    private static async Task<HttpResponseMessage> SelectAsync(HttpClient client, string csrf, Guid organizationId)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/session/organization")
        {
            Content = new StringContent(
                "{\"organizationId\":\"" + organizationId.ToString("D") + "\"}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        return await client.SendAsync(request);
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        HttpStatusCode status = response.StatusCode;
        response.Dispose();
        return status;
    }

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string path, string json)
    {
        HttpResponseMessage response = await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        HttpStatusCode status = response.StatusCode;
        response.Dispose();
        return status;
    }

    private static async Task<HttpStatusCode> ImportAsync(HttpClient client, string path, string csv, string? csrf = null)
    {
        HttpResponseMessage response = await ImportAsyncResponse(client, path, csv, csrf);
        HttpStatusCode status = response.StatusCode;
        response.Dispose();
        return status;
    }

    private static Task<HttpResponseMessage> ImportAsyncResponse(
        HttpClient client,
        string path,
        string csv,
        string? csrf,
        string fileName = "import.csv")
    {
        return client.SendAsync(ImportRequest(path, csv, csrf, fileName));
    }

    private static HttpRequestMessage ImportRequest(string path, string csv, string? csrf, string fileName = "import.csv")
    {
        MultipartFormDataContent content = new();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", fileName);
        content.Add(new StringContent("mBank"), "sourceSystem");
        HttpRequestMessage request = new(HttpMethod.Post, path) { Content = content };
        if (csrf is not null)
        {
            request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        }

        return request;
    }

    private static Task<HttpStatusCode> ExecuteAsync(HttpClient client, Guid invoiceId, Guid bankTransactionId, string csrf)
    {
        return SendStatusAsync(client, ExecuteRequest(invoiceId, bankTransactionId, Guid.NewGuid(), hold: false, csrf));
    }

    private static async Task<HttpStatusCode> SendStatusAsync(HttpClient client, HttpRequestMessage request)
    {
        HttpResponseMessage response = await client.SendAsync(request);
        HttpStatusCode status = response.StatusCode;
        response.Dispose();
        return status;
    }

    private static HttpRequestMessage ExecuteRequest(Guid invoiceId, Guid bankTransactionId, Guid key, bool hold, string? csrf = null)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/matches/batch/execute")
        {
            Content = new StringContent(
                "{\"pairs\":[{\"invoiceId\":\"" + invoiceId.ToString("D") + "\",\"bankTransactionId\":\"" + bankTransactionId.ToString("D") + "\"}]}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        if (hold)
        {
            request.Headers.TryAddWithoutValidation(HoldResponseStartupFilter.HeaderName, "1");
        }

        if (csrf is not null)
        {
            request.Headers.TryAddWithoutValidation(FinOpsDeskCsrf.HeaderName, csrf);
        }

        return request;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString() ?? string.Empty;
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        Assert.True(response.Headers.TryGetValues(name, out IEnumerable<string>? values));
        return Assert.Single(values!);
    }

    private static void AssertHeaderContains(HttpResponseMessage response, string name, string fragment)
    {
        Assert.True(response.Headers.TryGetValues(name, out IEnumerable<string>? values));
        Assert.Contains(values!, value => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string Policy(Dictionary<string, string> limited, string method, string path)
    {
        if (limited.TryGetValue(method + " " + path, out string? policy))
        {
            return policy;
        }

        string alternate = method + " " + path.TrimStart('/');
        Assert.True(limited.TryGetValue(alternate, out policy), string.Join(", ", limited.Keys));
        return policy!;
    }

    private static async Task WaitForRenewalAsync(
        Func<Task<HttpStatusCode>> send,
        HttpStatusCode success = HttpStatusCode.OK)
    {
        bool rejected = false;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        HttpStatusCode last = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await send();
            if (last == HttpStatusCode.TooManyRequests)
            {
                rejected = true;
            }
            else if (rejected && (last == success || last == HttpStatusCode.OK))
            {
                return;
            }

            if (rejected)
            {
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException("Renewal was not observed. Last status " + ((int)last).ToString(CultureInfo.InvariantCulture) + ".");
    }

    private sealed class TestPermitGate
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, bool> _open = [];
        private readonly List<string> _keys = [];
        private readonly TaskCompletionSource _endpointCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Keys
        {
            get
            {
                lock (_sync)
                {
                    return _keys.ToArray();
                }
            }
        }

        public Task EndpointCompleted => _endpointCompleted.Task;

        public void Open(Guid organizationId)
        {
            lock (_sync)
            {
                _open[organizationId] = true;
            }
        }

        public void Close(Guid organizationId)
        {
            lock (_sync)
            {
                _open[organizationId] = false;
            }
        }

        public bool IsOpen(Guid organizationId)
        {
            lock (_sync)
            {
                return _open.TryGetValue(organizationId, out bool open) && open;
            }
        }

        public void Record(string key)
        {
            lock (_sync)
            {
                _keys.Add(key);
            }
        }

        public void SignalEndpointCompleted()
        {
            _endpointCompleted.TrySetResult();
        }
    }

    private sealed class SelectorCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Selected()
        {
            Interlocked.Increment(ref _count);
        }
    }

    private sealed class ObservingPartitioner : IOrganizationRateLimitPartitioner
    {
        private readonly IOrganizationRateLimitPartitioner _inner;
        private readonly SelectorCounter _counter;

        public ObservingPartitioner(IOrganizationRateLimitPartitioner inner, SelectorCounter counter)
        {
            _inner = inner;
            _counter = counter;
        }

        public RateLimitPartition<string> Partition(HttpContext httpContext, string policy)
        {
            _counter.Selected();
            return _inner.Partition(httpContext, policy);
        }
    }

    private sealed class ScriptedReadPartitioner : IOrganizationRateLimitPartitioner
    {
        public RateLimitPartition<string> Partition(HttpContext httpContext, string policy)
        {
            if (policy != OrganizationRateLimitPolicies.MatchingReads)
            {
                throw new InvalidOperationException("This test partitioner only scripts matching reads.");
            }

            return RateLimitPartition.Get(
                OrganizationRateLimitPolicies.MatchingReads,
                _ => new ScriptedLimiter(
                [
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromMilliseconds(2200),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(-1),
                    null,
                ]));
        }
    }

    private sealed class ClosedImportsPartitioner : IOrganizationRateLimitPartitioner
    {
        private readonly IOrganizationRateLimitPartitioner _inner;
        private readonly SelectorCounter _counter;

        public ClosedImportsPartitioner(IOrganizationRateLimitPartitioner inner, SelectorCounter counter)
        {
            _inner = inner;
            _counter = counter;
        }

        public RateLimitPartition<string> Partition(HttpContext httpContext, string policy)
        {
            _counter.Selected();
            if (policy != OrganizationRateLimitPolicies.Imports)
            {
                return _inner.Partition(httpContext, policy);
            }

            return RateLimitPartition.Get(OrganizationRateLimitPolicies.Imports, _ => new ClosedLimiter());
        }
    }

    private sealed class GatePartitioner : IOrganizationRateLimitPartitioner
    {
        private readonly TestPermitGate _gate;
        private readonly AuthenticationCompositionState _state;
        private readonly string _claimType;

        public GatePartitioner(
            TestPermitGate gate,
            AuthenticationCompositionState state,
            IOptions<JwtBearerAuthenticationSettings> jwt)
        {
            _gate = gate;
            _state = state;
            _claimType = jwt.Value.OrganizationClaimType;
        }

        public RateLimitPartition<string> Partition(HttpContext httpContext, string policy)
        {
            if (!OrganizationRateLimitPolicies.TryReadOrganization(httpContext, _state, _claimType, out Guid organizationId))
            {
                throw new InvalidOperationException("A rate-limit partition requires a confirmed organization.");
            }

            string key = OrganizationRateLimitPolicies.PartitionKey(policy, organizationId);
            _gate.Record(key);
            return RateLimitPartition.Get(key, _ => new GateLimiter(_gate, organizationId));
        }
    }

    private sealed class GateLimiter : RateLimiter
    {
        private readonly TestPermitGate _gate;
        private readonly Guid _organizationId;

        public GateLimiter(TestPermitGate gate, Guid organizationId)
        {
            _gate = gate;
            _organizationId = organizationId;
        }

        public override TimeSpan? IdleDuration => TimeSpan.Zero;

        public override RateLimiterStatistics? GetStatistics()
        {
            return null;
        }

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            return _gate.IsOpen(_organizationId) ? OpenLease.Instance : ClosedLease.Instance;
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            return new ValueTask<RateLimitLease>(AttemptAcquireCore(permitCount));
        }
    }

    private sealed class OpenLease : RateLimitLease
    {
        public static readonly OpenLease Instance = new();

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private sealed class CountingHandlers : IFinancialHandlerObservation
    {
        private int _imports;
        private int _executes;

        public int Imports => _imports;

        public int Executes => _executes;

        public void OnImport()
        {
            Interlocked.Increment(ref _imports);
        }

        public void OnExecute()
        {
            Interlocked.Increment(ref _executes);
        }
    }

    private sealed class RequestReadCounter
    {
        private int _count;

        public int Count => _count;

        public void Add()
        {
            Interlocked.Increment(ref _count);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _count, 0);
        }
    }

    private sealed class CountingBodyStartupFilter : IStartupFilter
    {
        private readonly RequestReadCounter _counter;

        public CountingBodyStartupFilter(RequestReadCounter counter)
        {
            _counter = counter;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, requestNext) =>
                {
                    context.Request.Body = new CountingStream(context.Request.Body, _counter);
                    return requestNext(context);
                });
                next(app);
            };
        }
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly RequestReadCounter _counter;

        public CountingStream(Stream inner, RequestReadCounter counter)
        {
            _inner = inner;
            _counter = counter;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _counter.Add();
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _counter.Add();
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _counter.Add();
            return _inner.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class HoldResponseStartupFilter : IStartupFilter
    {
        public const string HeaderName = "X-FinOpsDesk-Test-Hold-Response";

        private readonly TestPermitGate _gate;

        public HoldResponseStartupFilter(TestPermitGate gate)
        {
            _gate = gate;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, requestNext) =>
                {
                    if (!context.Request.Headers.ContainsKey(HeaderName))
                    {
                        await requestNext(context);
                        return;
                    }

                    Stream original = context.Response.Body;
                    context.Response.Body = new MemoryStream();
                    try
                    {
                        try
                        {
                            await requestNext(context);
                        }
                        finally
                        {
                            _gate.SignalEndpointCompleted();
                        }

                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                    finally
                    {
                        context.Response.Body = original;
                    }
                });
                next(app);
            };
        }
    }

    private sealed class ClosedLimiter : RateLimiter
    {
        public override TimeSpan? IdleDuration => TimeSpan.Zero;

        public override RateLimiterStatistics? GetStatistics()
        {
            return null;
        }

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            return ClosedLease.Instance;
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            return new ValueTask<RateLimitLease>(ClosedLease.Instance);
        }
    }

    private sealed class ClosedLease : RateLimitLease
    {
        public static readonly ClosedLease Instance = new();

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = TimeSpan.FromSeconds(1);
                return true;
            }

            metadata = null;
            return false;
        }
    }

    private sealed class ScriptedLimiter : RateLimiter
    {
        private readonly Queue<TimeSpan?> _retries;

        public ScriptedLimiter(IEnumerable<TimeSpan?> retries)
        {
            _retries = new Queue<TimeSpan?>();
            foreach (TimeSpan? retry in retries)
            {
                // The limiter acquires twice for one rejection.
                _retries.Enqueue(retry);
                _retries.Enqueue(retry);
            }
        }

        public override TimeSpan? IdleDuration => TimeSpan.Zero;

        public override RateLimiterStatistics? GetStatistics()
        {
            return null;
        }

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            TimeSpan? retry = _retries.Count == 0 ? null : _retries.Dequeue();
            return new ScriptedLease(retry);
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            return new ValueTask<RateLimitLease>(AttemptAcquireCore(permitCount));
        }
    }

    private sealed class ScriptedLease : RateLimitLease
    {
        private readonly TimeSpan? _retry;

        public ScriptedLease(TimeSpan? retry)
        {
            _retry = retry;
        }

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => _retry is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_retry is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = _retry;
                return true;
            }

            metadata = null;
            return false;
        }
    }

    private sealed class SucceedAllRequirementsHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            foreach (IAuthorizationRequirement requirement in context.Requirements)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}