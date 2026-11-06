using FinOpsDesk.IntegrationTests.Api;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

public sealed partial class CookieFinancialReadBrowserTests
{
    private static readonly Guid SouthOrganizationId = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");

    [Fact]
    public async Task Switching_from_viewer_to_administrator_removes_the_previous_invoice()
    {
        await using FinanceJourney journey = await FinanceJourney.StartAsync();
        const string northInvoice = "COOKIE-NORTH-INV";
        await SeedOrganizationAsync(
            journey.Database.ConnectionString,
            journey.Issuer,
            OrganizationId,
            "North Ledger",
            OrganizationRole.Viewer,
            northInvoice);
        await SeedAdditionalOrganizationAsync(
            journey.Database.ConnectionString,
            SouthOrganizationId,
            "South Ledger",
            OrganizationRole.OrganizationAdmin,
            invoiceNumber: null);

        IPage page = await journey.OpenPageAsync();
        await SignInAndOpenNorthAsync(page, journey.BrowserOrigin);
        await page.GetByText(northInvoice).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Switch to South Ledger" }).ClickAsync();
        await page.GetByText("Financial access is not available for this organization.").WaitForAsync();
        Assert.Equal(0, await page.GetByText(northInvoice).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Invoices" }).CountAsync());
        Assert.NotNull(await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ElementHandleAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).CountAsync());
    }

    [Fact]
    public async Task Revoking_viewer_removes_financial_access_without_signing_out()
    {
        await using FinanceJourney journey = await FinanceJourney.StartAsync();
        const string invoiceNumber = "COOKIE-REVOKE-INV";
        await SeedOrganizationAsync(
            journey.Database.ConnectionString,
            journey.Issuer,
            OrganizationId,
            "North Ledger",
            OrganizationRole.Viewer,
            invoiceNumber);

        IPage page = await journey.OpenPageAsync();
        await SignInAndOpenNorthAsync(page, journey.BrowserOrigin);
        await page.GetByText(invoiceNumber).WaitForAsync();
        string cookieBefore = await ReadSessionCookieAsync(journey.Context, journey.BrowserOrigin);

        await RevokeViewerAsync(journey.Database.ConnectionString);
        await RevalidateAsync(page);
        await page.GetByText("Financial access is not available for this organization.").WaitForAsync();

        Assert.Equal(0, await page.GetByText(invoiceNumber).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Invoices" }).CountAsync());
        Assert.Equal(cookieBefore, await ReadSessionCookieAsync(journey.Context, journey.BrowserOrigin));
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).WaitForAsync();
    }

    [Fact]
    public async Task Role_lookup_outage_hides_finance_and_recovery_restores_it()
    {
        await using FinanceJourney journey = await FinanceJourney.StartAsync();
        const string invoiceNumber = "COOKIE-OUTAGE-INV";
        await SeedOrganizationAsync(
            journey.Database.ConnectionString,
            journey.Issuer,
            OrganizationId,
            "North Ledger",
            OrganizationRole.Viewer,
            invoiceNumber);
        IPage page = await journey.OpenPageAsync();
        await SignInAndOpenNorthAsync(page, journey.BrowserOrigin);
        await page.GetByText(invoiceNumber).WaitForAsync();
        string cookieBefore = await ReadSessionCookieAsync(journey.Context, journey.BrowserOrigin);

        try
        {
            await RenameRoleTableAsync(journey.Database.ConnectionString, "OrganizationRoleAssignments", "OrganizationRoleAssignments_offline");
            await RevalidateAsync(page);
            await page.GetByText("Authorization could not be verified. Financial records stay hidden. This is not a sign-out.").WaitForAsync();
            Assert.Equal(0, await page.GetByText(invoiceNumber).CountAsync());
            Assert.Equal(cookieBefore, await ReadSessionCookieAsync(journey.Context, journey.BrowserOrigin));
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).CountAsync());
        }
        finally
        {
            await RenameRoleTableAsync(journey.Database.ConnectionString, "OrganizationRoleAssignments_offline", "OrganizationRoleAssignments");
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Try again" }).ClickAsync();
        await page.GetByText(invoiceNumber).WaitForAsync();
        Assert.Equal(cookieBefore, await ReadSessionCookieAsync(journey.Context, journey.BrowserOrigin));
    }

    [Fact]
    public async Task Viewer_reads_a_seeded_review_case_without_posting()
    {
        await using FinanceJourney journey = await FinanceJourney.StartAsync();
        await SeedOrganizationAsync(
            journey.Database.ConnectionString,
            journey.Issuer,
            OrganizationId,
            "North Ledger",
            OrganizationRole.Viewer,
            invoiceNumber: null);
        await SeedZeroBalanceReviewCaseAsync(journey.Database.ConnectionString, OrganizationId);

        IPage page = await journey.OpenPageAsync();
        int reviewPosts = 0;
        int auditGets = 0;
        page.Request += (_, request) =>
        {
            if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                && request.Url.Contains("/api/review-cases", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref reviewPosts);
            }

            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && request.Url.Contains("/audit", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref auditGets);
            }
        };

        await SignInAndOpenNorthAsync(page, journey.BrowserOrigin);
        await page.GetByRole(AriaRole.Link, new() { Name = "Review cases" }).ClickAsync();
        await page.GetByText("watch the zero balance").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Show review case", Exact = false }).ClickAsync();
        await page.GetByText("Invoice outstanding 0.00 PLN").WaitForAsync();
        await page.GetByText("Bank transaction unapplied 10.0001 PLN").WaitForAsync();
        await page.GetByText("not one atomic snapshot").WaitForAsync();
        await page.GetByText("This screen only reads them.").WaitForAsync();
        ILocator summary = page.GetByRole(AriaRole.Region, new() { Name = "Organization summary" });
        await summary.GetByText("All cases 1", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Open 1", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Resolved 0", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Dismissed 0", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Open competing matches 1", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Open investigations 0", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("These counts cover every persisted review case in this organization.").WaitForAsync();
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "View audit" }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Open review case" }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Send the same opening again" }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Resolve", Exact = true }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Dismiss", Exact = true }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Send the same decision again" }).CountAsync());
        Assert.Equal(0, reviewPosts);
        Assert.Equal(0, auditGets);
    }

    [Fact]
    public async Task Operator_reads_a_seeded_review_case_audit_without_posting()
    {
        await using FinanceJourney journey = await FinanceJourney.StartAsync();
        await SeedOrganizationAsync(
            journey.Database.ConnectionString,
            journey.Issuer,
            OrganizationId,
            "North Ledger",
            OrganizationRole.Operator,
            invoiceNumber: null);
        await SeedZeroBalanceReviewCaseAsync(
            journey.Database.ConnectionString,
            OrganizationId,
            "e2e-review-audit-actor");

        IPage page = await journey.OpenPageAsync();
        int reviewPosts = 0;
        page.Request += (_, request) =>
        {
            if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                && request.Url.Contains("/api/review-cases", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref reviewPosts);
            }
        };

        await SignInAndOpenNorthAsync(page, journey.BrowserOrigin);
        await page.GetByRole(AriaRole.Link, new() { Name = "Review cases" }).ClickAsync();
        await page.GetByText("watch the zero balance").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Show review case", Exact = false }).ClickAsync();
        await page.GetByText("Invoice outstanding 0.00 PLN").WaitForAsync();
        await page.GetByText("This screen only reads them.").WaitForAsync();
        ILocator summary = page.GetByRole(AriaRole.Region, new() { Name = "Organization summary" });
        await summary.GetByText("All cases 1", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Open 1", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Resolved 0", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Dismissed 0", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Open competing matches 1", new() { Exact = true }).WaitForAsync();
        await summary.GetByText("Open investigations 0", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Open review case" }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Send the same opening again" }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Resolve", Exact = true }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Dismiss", Exact = true }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Send the same decision again" }).CountAsync());
        Assert.Equal(0, reviewPosts);

        await page.GetByRole(AriaRole.Button, new() { Name = "View audit" }).ClickAsync();
        ILocator history = page.GetByRole(AriaRole.Region, new() { Name = "Stored history" });
        await history.GetByText("Opened").WaitForAsync();
        await history.GetByText("Actor e2e-review-audit-actor").WaitForAsync();
        await history.GetByText("Reason CompetingMatch").WaitForAsync();
        await history.GetByText("Note watch the zero balance").WaitForAsync();
        await history.GetByText("Stored history is separate from the current case and from the balances above.").WaitForAsync();
        Assert.Equal(0, await history.GetByText("OIDC Operator").CountAsync());
        Assert.Equal(0, await history.GetByText("The opening entry is not in the stored history.").CountAsync());
        Assert.Equal(0, reviewPosts);
    }

    private static async Task SignInAndOpenNorthAsync(IPage page, string browserOrigin)
    {
        await page.GotoAsync(browserOrigin);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).ClickAsync();
        await page.GetByText("Active organization: North Ledger").WaitForAsync();
    }

    private static async Task RevalidateAsync(IPage page)
    {
        await page.EvaluateAsync(
            """
            () => {
              window.dispatchEvent(new Event('blur'));
              window.dispatchEvent(new Event('focus'));
            }
            """);
    }

    private static async Task<string> ReadSessionCookieAsync(IBrowserContext context, string origin)
    {
        IReadOnlyList<BrowserContextCookiesResult> cookies = await context.CookiesAsync(origin);
        BrowserContextCookiesResult cookie = cookies.Single(candidate => candidate.Name == "__Host-finopsdesk-session");
        Assert.False(string.IsNullOrWhiteSpace(cookie.Value));
        return cookie.Value;
    }

    private static async Task RevokeViewerAsync(string connectionString)
    {
        await using AccessDbContext access = E2EDatabase.CreateAccessContext(connectionString);
        OrganizationRoleAssignment assignment = await access.OrganizationRoleAssignments.SingleAsync();
        assignment.Revoke(DateTimeOffset.UtcNow, RoleActor.DeploymentBootstrap("e2e-revoke"));
        await access.SaveChangesAsync();
    }

    private static async Task RenameRoleTableAsync(string connectionString, string from, string to)
    {
        if (from is not ("OrganizationRoleAssignments" or "OrganizationRoleAssignments_offline")
            || to is not ("OrganizationRoleAssignments" or "OrganizationRoleAssignments_offline"))
        {
            throw new InvalidOperationException("Unexpected access table rename.");
        }

        await using AccessDbContext access = E2EDatabase.CreateAccessContext(connectionString);
        if (from == "OrganizationRoleAssignments" && to == "OrganizationRoleAssignments_offline")
        {
            await access.Database.ExecuteSqlAsync($"EXEC sp_rename N'access.OrganizationRoleAssignments', N'OrganizationRoleAssignments_offline';");
            return;
        }

        await access.Database.ExecuteSqlAsync($"EXEC sp_rename N'access.OrganizationRoleAssignments_offline', N'OrganizationRoleAssignments';");
    }

    private static async Task SeedOrganizationAsync(
        string connectionString,
        string issuer,
        Guid organizationId,
        string organizationName,
        OrganizationRole role,
        string? invoiceNumber)
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        Guid userId = Guid.NewGuid();
        Guid membershipId = Guid.NewGuid();
        await using (AccessDbContext access = E2EDatabase.CreateAccessContext(connectionString))
        {
            access.Organizations.Add(Organization.Create(organizationId, organizationName, createdAt));
            access.ApplicationUsers.Add(ApplicationUser.Create(
                userId,
                issuer,
                OidcProtocolStub.Subject,
                OidcProtocolStub.DisplayName,
                createdAt));
            access.OrganizationMemberships.Add(OrganizationMembership.Grant(
                membershipId,
                userId,
                organizationId,
                createdAt));
            access.OrganizationRoleAssignments.Add(OrganizationRoleAssignment.Create(
                Guid.NewGuid(),
                membershipId,
                role,
                createdAt.AddSeconds(1),
                RoleActor.DeploymentBootstrap("e2e-role")));
            await access.SaveChangesAsync();
        }

        if (invoiceNumber is null)
        {
            return;
        }

        await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(connectionString);
        reconciliation.Invoices.Add(new Invoice(
            InvoiceId.New(),
            new OrganizationId(organizationId),
            invoiceNumber,
            new Money(12.50m, new CurrencyCode("PLN"))));
        await reconciliation.SaveChangesAsync();
    }

    private static Task SeedZeroBalanceReviewCaseAsync(string connectionString, Guid organizationId)
    {
        return SeedZeroBalanceReviewCaseAsync(connectionString, organizationId, auditActor: null);
    }

    private static async Task SeedZeroBalanceReviewCaseAsync(
        string connectionString,
        Guid organizationId,
        string? auditActor)
    {
        OrganizationId tenant = new(organizationId);
        CurrencyCode pln = new("PLN");
        Invoice invoice = new(InvoiceId.New(), tenant, "COOKIE-REVIEW-INV", new Money(10m, pln));
        Result allocated = invoice.Allocate(new Money(10m, pln));
        if (allocated.IsFailure)
        {
            throw new InvalidOperationException("The review-case fixture could not allocate the invoice.");
        }

        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(10.0001m, pln),
            new DateOnly(2026, 11, 5),
            "COOKIE-REVIEW-BANK");
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            tenant,
            ReviewCaseReason.CompetingMatch,
            invoice.Id,
            bank.Id,
            ReviewCaseSource.Manual,
            "watch the zero balance",
            DateTimeOffset.Parse("2026-11-05T08:00:00Z"));

        await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(connectionString);
        reconciliation.Invoices.Add(invoice);
        reconciliation.BankTransactions.Add(bank);
        await reconciliation.SaveChangesAsync();
        reconciliation.ReviewCases.Add(reviewCase);
        await reconciliation.SaveChangesAsync();
        if (auditActor is null)
        {
            return;
        }

        reconciliation.ReviewCaseAuditEntries.Add(ReviewCaseAuditEntry.ForOpened(
            ReviewCaseAuditEntryId.New(),
            reviewCase,
            auditActor));
        await reconciliation.SaveChangesAsync();
    }

    private static async Task SeedAdditionalOrganizationAsync(
        string connectionString,
        Guid organizationId,
        string organizationName,
        OrganizationRole role,
        string? invoiceNumber)
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        await using (AccessDbContext access = E2EDatabase.CreateAccessContext(connectionString))
        {
            ApplicationUser user = await access.ApplicationUsers.SingleAsync();
            Guid membershipId = Guid.NewGuid();
            access.Organizations.Add(Organization.Create(organizationId, organizationName, createdAt));
            access.OrganizationMemberships.Add(OrganizationMembership.Grant(
                membershipId,
                user.Id,
                organizationId,
                createdAt));
            access.OrganizationRoleAssignments.Add(OrganizationRoleAssignment.Create(
                Guid.NewGuid(),
                membershipId,
                role,
                createdAt.AddSeconds(1),
                RoleActor.DeploymentBootstrap("e2e-role")));
            await access.SaveChangesAsync();
        }

        if (invoiceNumber is null)
        {
            return;
        }

        await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(connectionString);
        reconciliation.Invoices.Add(new Invoice(
            InvoiceId.New(),
            new OrganizationId(organizationId),
            invoiceNumber,
            new Money(9m, new CurrencyCode("PLN"))));
        await reconciliation.SaveChangesAsync();
    }

    private sealed class FinanceJourney : IAsyncDisposable
    {
        private readonly string _certificatePath;
        private readonly OidcProtocolStub _stub;
        private readonly E2EApiProcess _api;
        private readonly E2EViteProcess _vite;
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private bool _disposed;

        private FinanceJourney(
            E2EDatabase database,
            string certificatePath,
            OidcProtocolStub stub,
            E2EApiProcess api,
            E2EViteProcess vite,
            IPlaywright playwright,
            IBrowser browser,
            string browserOrigin)
        {
            Database = database;
            _certificatePath = certificatePath;
            _stub = stub;
            _api = api;
            _vite = vite;
            _playwright = playwright;
            _browser = browser;
            BrowserOrigin = browserOrigin;
            Issuer = stub.Authority;
        }

        public E2EDatabase Database { get; }

        public string BrowserOrigin { get; }

        public string Issuer { get; }

        public IBrowserContext Context { get; private set; } = null!;

        public static async Task<FinanceJourney> StartAsync()
        {
            string certificatePath = Path.Combine(
                Path.GetTempPath(),
                "finopsdesk-cookie-finance-" + Guid.NewGuid().ToString("N") + ".crt");
            E2EDatabase? database = null;
            OidcProtocolStub? stub = null;
            E2EApiProcess? api = null;
            E2EViteProcess? vite = null;
            IPlaywright? playwright = null;
            IBrowser? browser = null;
            try
            {
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
                return new FinanceJourney(database, certificatePath, stub, api, vite, playwright, browser, browserOrigin);
            }
            catch (Exception exception)
            {
                api ??= exception is E2EApiStartupException startup ? startup.Api : null;
                if (browser is not null)
                {
                    await browser.CloseAsync();
                }

                playwright?.Dispose();
                if (vite is not null)
                {
                    await vite.DisposeAsync();
                }

                bool apiStopped = api is null;
                if (api is not null)
                {
                    try
                    {
                        await api.DisposeAsync();
                    }
                    catch (Exception disposeFailure)
                    {
                        exception = new AggregateException(exception, disposeFailure);
                    }

                    apiStopped = api.TerminationEstablished;
                }

                if (stub is not null)
                {
                    await stub.DisposeAsync();
                }

                if (database is not null && apiStopped)
                {
                    await database.DisposeAsync();
                }

                if (File.Exists(certificatePath))
                {
                    File.Delete(certificatePath);
                }

                if (exception is AggregateException)
                {
                    throw exception;
                }

                throw;
            }
        }

        public async Task<IPage> OpenPageAsync()
        {
            Context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = false,
                BaseURL = BrowserOrigin,
            });
            IPage page = await Context.NewPageAsync();
            page.SetDefaultTimeout(60_000);
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (Context is not null)
            {
                await Context.CloseAsync();
            }

            await _browser.CloseAsync();
            _playwright.Dispose();
            await _vite.DisposeAsync();
            await _api.DisposeAsync();
            await _stub.DisposeAsync();
            await Database.DisposeAsync();
            if (File.Exists(_certificatePath))
            {
                File.Delete(_certificatePath);
            }
        }
    }
}