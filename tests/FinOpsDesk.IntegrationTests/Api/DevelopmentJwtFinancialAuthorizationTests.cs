using System.Net;
using System.Net.Http.Headers;
using System.Text;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class DevelopmentJwtFinancialAuthorizationTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public DevelopmentJwtFinancialAuthorizationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(
            fixture.ConnectionString,
            financialReadAuthorizationEnabled: true,
            financialWriteAuthorizationEnabled: true);
    }

    [Fact]
    public async Task Enabled_cookie_setting_does_not_change_jwt_reads_or_writes()
    {
        AuthenticationCompositionState state = _factory.Services.GetRequiredService<AuthenticationCompositionState>();
        Assert.False(state.UsesProductionCookie);
        Assert.False(state.ProductionFinancialAccessFailClosed);
        Assert.False(state.ProductionFinancialWritesEnabled);

        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = new(InvoiceId.New(), organizationId, "JWT-INV", new Money(12m, PersistenceTestData.Pln));
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            await context.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        HttpResponseMessage invoices = await client.GetAsync("/api/invoices");
        string invoiceBody = await invoices.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, invoices.StatusCode);
        Assert.Contains("JWT-INV", invoiceBody, StringComparison.Ordinal);

        using HttpRequestMessage confirm = new(HttpMethod.Post, "/api/matches/confirm")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        confirm.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        HttpResponseMessage confirmResponse = await client.SendAsync(confirm);
        Assert.NotEqual(HttpStatusCode.Forbidden, confirmResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, confirmResponse.StatusCode);

        using HttpRequestMessage allocate = new(HttpMethod.Post, "/api/allocations")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        allocate.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        HttpResponseMessage allocateResponse = await client.SendAsync(allocate);
        Assert.NotEqual(HttpStatusCode.Forbidden, allocateResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allocateResponse.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}