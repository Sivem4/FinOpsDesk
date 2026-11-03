using System.Text.Json;

using FinOpsDesk.IntegrationTests.Api;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed partial class CookieFinancialReadBrowserTests
{
    private static readonly Guid OrganizationId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");

    [Fact]
    public async Task Viewer_reads_a_seeded_invoice_and_cookie_post_stays_forbidden()
    {
        string? certificatePath = null;
        E2EDatabase? database = null;
        OidcProtocolStub? stub = null;
        E2EApiProcess? api = null;
        E2EViteProcess? vite = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        Exception? testFailure = null;
        const string invoiceNumber = "COOKIE-VIEWER-INV";

        try
        {
            certificatePath = Path.Combine(
                Path.GetTempPath(),
                "finopsdesk-cookie-finance-" + Guid.NewGuid().ToString("N") + ".crt");
            DevelopmentCertificateExport.WritePublicPem(certificatePath);
            database = await E2EDatabase.CreateAsync(CancellationToken.None);
            int browserPort = LoopbackPort.Reserve();
            string browserOrigin = "http://localhost:" + browserPort;
            stub = await OidcProtocolStub.StartAsync(browserOrigin + "/signin-oidc");
            api = await E2EApiProcess.StartCookieAsync(
                database.ConnectionString,
                stub.Authority,
                browserOrigin,
                OidcProtocolStub.ClientId,
                OidcProtocolStub.ClientSecret,
                financialReadAuthorizationEnabled: true,
                CancellationToken.None);
            await SeedViewerFinanceAsync(database.ConnectionString, stub.Authority, invoiceNumber);
            vite = await E2EViteProcess.StartAsync(
                api.Origin,
                new E2EViteLaunch
                {
                    Host = "localhost",
                    Port = browserPort,
                    CookieMode = true,
                    NodeExtraCaCerts = certificatePath,
                },
                CancellationToken.None);
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 30_000,
            });
            IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = false,
                BaseURL = browserOrigin,
            });
            IPage page = await context.NewPageAsync();
            page.SetDefaultTimeout(60_000);
            int bearerCount = 0;
            page.Request += (_, request) =>
            {
                if (request.Headers.TryGetValue("authorization", out string? value)
                    && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    bearerCount++;
                }
            };

            await page.GotoAsync(browserOrigin);
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).ClickAsync();
            await page.GetByText("Active organization: North Ledger").WaitForAsync();

            string sessionJson = await page.EvaluateAsync<string>(
                @"async () => {
                    const response = await fetch('/api/session', {
                      credentials: 'same-origin',
                      headers: {
                        'Accept': 'application/json',
                        'X-FinOpsDesk-Session-Contract': '2'
                      },
                      cache: 'no-store'
                    });
                    const body = await response.json();
                    return JSON.stringify({ status: response.status, body });
                }");
            using JsonDocument sessionDocument = JsonDocument.Parse(sessionJson);
            JsonElement session = sessionDocument.RootElement;
            Assert.Equal(200, session.GetProperty("status").GetInt32());
            JsonElement body = session.GetProperty("body");
            Assert.True(body.GetProperty("authenticated").GetBoolean());
            Assert.True(
                body.GetProperty("financialAccess").GetBoolean(),
                "Session contract v2 did not enable financialAccess for the seeded Viewer. Body=" + body);
            Assert.Contains(
                "invoices.read",
                body.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()));

            await page.GetByRole(AriaRole.Heading, new() { Name = "Invoices" }).WaitForAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Invoices" }).WaitForAsync();
            await page.GetByText(invoiceNumber).WaitForAsync();
            Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Import invoices" }).CountAsync());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Import invoices" }).CountAsync());
            Assert.Equal(0, bearerCount);

            string csrf = body.GetProperty("csrfToken").GetString()
                ?? throw new InvalidOperationException("Session response omitted csrfToken.");
            await using IAPIResponse denied = await page.APIRequest.PostAsync(
                browserOrigin + "/api/matches/confirm",
                new()
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/json",
                        ["X-CSRF-Token"] = csrf,
                        ["Idempotency-Key"] = Guid.NewGuid().ToString("D"),
                    },
                    DataObject = new { },
                });
            Assert.Equal(403, denied.Status);
            Assert.Equal(0, bearerCount);

            await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(
                database.ConnectionString);
            Assert.Equal(1, await reconciliation.Invoices.CountAsync());
            Assert.Equal(0, await reconciliation.Allocations.CountAsync());
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            if (browser is not null)
            {
                await browser.CloseAsync();
            }

            playwright?.Dispose();
            if (vite is not null)
            {
                await vite.DisposeAsync();
            }

            if (api is not null)
            {
                await api.DisposeAsync();
            }

            if (stub is not null)
            {
                await stub.DisposeAsync();
            }

            if (database is not null)
            {
                await database.DisposeAsync();
            }

            if (certificatePath is not null && File.Exists(certificatePath))
            {
                File.Delete(certificatePath);
            }
        }

        if (testFailure is not null)
        {
            throw testFailure;
        }
    }

    private static async Task SeedViewerFinanceAsync(string connectionString, string issuer, string invoiceNumber)
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        Guid userId = Guid.NewGuid();
        Guid membershipId = Guid.NewGuid();
        await using (AccessDbContext access = E2EDatabase.CreateAccessContext(connectionString))
        {
            access.Organizations.Add(Organization.Create(OrganizationId, "North Ledger", createdAt));
            access.ApplicationUsers.Add(ApplicationUser.Create(
                userId,
                issuer,
                OidcProtocolStub.Subject,
                OidcProtocolStub.DisplayName,
                createdAt));
            access.OrganizationMemberships.Add(OrganizationMembership.Grant(
                membershipId,
                userId,
                OrganizationId,
                createdAt));
            access.OrganizationRoleAssignments.Add(OrganizationRoleAssignment.Create(
                Guid.NewGuid(),
                membershipId,
                OrganizationRole.Viewer,
                createdAt.AddSeconds(1),
                RoleActor.DeploymentBootstrap("e2e-viewer")));
            await access.SaveChangesAsync();
        }

        await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(connectionString);
        reconciliation.Invoices.Add(new Invoice(
            InvoiceId.New(),
            new OrganizationId(OrganizationId),
            invoiceNumber,
            new Money(12.50m, new CurrencyCode("PLN"))));
        await reconciliation.SaveChangesAsync();
    }
}