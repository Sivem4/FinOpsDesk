using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class ImportEndpointTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ImportEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Import_requires_authentication()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/imports/invoices",
            InvoiceForm("ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV,1.00,PLN\n", "mBank"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Import_requires_tenant_membership()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage response = await client.PostAsync(
            "/api/imports/bank-transactions",
            BankForm(
                "ExternalId,BookingDate,Amount,Currency,Reference\nTXN-1,2026-08-24,1.00,PLN,\n",
                "mBank"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_import_returns_created_and_replay_returns_ok()
    {
        Guid organizationId = Guid.NewGuid();
        Guid untrustedOrganizationId = Guid.NewGuid();
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId));
        const string csv = "ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV/1,10.00,PLN\n";

        HttpResponseMessage created = await client.PostAsync(
            "/api/imports/invoices",
            InvoiceForm(csv, "mBank", untrustedOrganizationId));
        HttpResponseMessage replayed = await client.PostAsync(
            "/api/imports/invoices",
            InvoiceForm(csv, "mBank", untrustedOrganizationId));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

        using JsonDocument createdDocument = await ReadJsonAsync(created);
        using JsonDocument replayedDocument = await ReadJsonAsync(replayed);
        JsonElement createdBody = createdDocument.RootElement;
        JsonElement replayedBody = replayedDocument.RootElement;

        Assert.False(createdBody.TryGetProperty("organizationId", out _));
        Assert.Equal("Invoice", createdBody.GetProperty("importType").GetString());
        Assert.Equal("MBANK", createdBody.GetProperty("sourceSystem").GetString());
        Assert.Equal("Completed", createdBody.GetProperty("status").GetString());
        Assert.Equal(1, createdBody.GetProperty("importedRows").GetInt32());
        Assert.False(createdBody.GetProperty("replayed").GetBoolean());
        Assert.Equal(64, createdBody.GetProperty("fileHash").GetString()!.Length);
        Assert.Empty(createdBody.GetProperty("rowErrors").EnumerateArray());
        Assert.True(replayedBody.GetProperty("replayed").GetBoolean());
        Assert.Equal(
            createdBody.GetProperty("importBatchId").GetGuid(),
            replayedBody.GetProperty("importBatchId").GetGuid());

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(
            1,
            await context.Invoices.CountAsync(invoice => invoice.OrganizationId == new OrganizationId(organizationId)));
        Assert.Equal(
            0,
            await context.Invoices.CountAsync(invoice =>
                invoice.OrganizationId == new OrganizationId(untrustedOrganizationId)));
    }

    [Fact]
    public async Task Missing_file_and_invalid_source_system_are_bad_requests()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        using var missingFile = new MultipartFormDataContent();
        missingFile.Add(new StringContent("mBank"), "sourceSystem");
        HttpResponseMessage missing = await client.PostAsync("/api/imports/invoices", missingFile);

        HttpResponseMessage invalidSource = await client.PostAsync(
            "/api/imports/invoices",
            InvoiceForm("ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV,1.00,PLN\n", " "));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidSource.StatusCode);
        Assert.Equal(
            ReconciliationApplicationErrors.ImportFileRequired.Code,
            await ReadCodeAsync(missing));
        Assert.Equal(
            ReconciliationApplicationErrors.ImportInvalidSourceSystem.Code,
            await ReadCodeAsync(invalidSource));
    }

    [Fact]
    public async Task Invalid_csv_is_a_bad_request_and_is_not_persisted()
    {
        Guid organizationId = Guid.NewGuid();
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId));

        HttpResponseMessage response = await client.PostAsync(
            "/api/imports/invoices",
            InvoiceForm("ExternalId,InvoiceNumber,Amount,Currency\n\n", "mBank"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ReconciliationApplicationErrors.ImportInvalidFile.Code, await ReadCodeAsync(response));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(
            0,
            await context.ImportBatches.CountAsync(batch =>
                batch.OrganizationId == new OrganizationId(organizationId)));
    }

    [Fact]
    public async Task Oversized_file_returns_413()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));
        byte[] bytes = new byte[ImportLimits.MaxFileBytes + 1];

        HttpResponseMessage response = await client.PostAsync(
            "/api/imports/invoices",
            InvoiceForm(bytes, "mBank"));

        Assert.Equal((HttpStatusCode)413, response.StatusCode);
        Assert.Equal(ReconciliationApplicationErrors.ImportFileTooLarge.Code, await ReadCodeAsync(response));
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private static MultipartFormDataContent InvoiceForm(
        string csv,
        string sourceSystem,
        Guid? organizationId = null)
    {
        return InvoiceForm(Encoding.UTF8.GetBytes(csv), sourceSystem, organizationId);
    }

    private static MultipartFormDataContent InvoiceForm(
        byte[] csv,
        string sourceSystem,
        Guid? organizationId = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(csv);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "invoices.csv");
        content.Add(new StringContent(sourceSystem), "sourceSystem");

        if (organizationId is not null)
        {
            content.Add(new StringContent(organizationId.Value.ToString()), "organizationId");
        }

        return content;
    }

    private static MultipartFormDataContent BankForm(string csv, string sourceSystem)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "transactions.csv");
        content.Add(new StringContent(sourceSystem), "sourceSystem");

        return content;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = await ReadJsonAsync(response);

        return document.RootElement.GetProperty("code").GetString()!;
    }
}