using System.Text.Json;

using FinOpsDesk.IntegrationTests.Api;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class CookieFinancialWriteBrowserTests
{
    private static readonly Guid OrganizationId = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");

    [Fact]
    public async Task Operator_confirms_an_exact_match_and_import_stays_forbidden()
    {
        string? certificatePath = null;
        E2EDatabase? database = null;
        OidcProtocolStub? stub = null;
        E2EApiProcess? api = null;
        E2EViteProcess? vite = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        Exception? testFailure = null;

        try
        {
            certificatePath = Path.Combine(
                Path.GetTempPath(),
                "finopsdesk-cookie-write-" + Guid.NewGuid().ToString("N") + ".crt");
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
                CancellationToken.None,
                financialWriteAuthorizationEnabled: true);
            await SeedOperatorPairAsync(database.ConnectionString, stub.Authority);
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
            await page.GotoAsync(browserOrigin);
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
            await page.GetByText("Allocation recorded.").WaitForAsync();

            await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(database.ConnectionString);
            Assert.Equal(1, await reconciliation.Allocations.CountAsync());

            string sessionJson = await page.EvaluateAsync<string>(
                @"async () => {
                    const response = await fetch('/api/session', {
                      credentials: 'same-origin',
                      headers: { 'Accept': 'application/json', 'X-FinOpsDesk-Session-Contract': '2' },
                      cache: 'no-store'
                    });
                    return JSON.stringify(await response.json());
                }");
            using JsonDocument session = JsonDocument.Parse(sessionJson);
            string? csrf = session.RootElement.GetProperty("csrfToken").GetString();
            await using IAPIResponse denied = await page.APIRequest.PostAsync(
                browserOrigin + "/api/matches/batch/execute",
                new()
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/json",
                        ["X-CSRF-Token"] = csrf ?? string.Empty,
                        ["Idempotency-Key"] = Guid.NewGuid().ToString("D"),
                    },
                    Data = "{\"pairs\":[]}",
                });
            Assert.Equal(403, denied.Status);
            foreach (string path in new[]
            {
                "/api/imports/invoices",
                "/api/imports/bank-transactions",
                "/api/review-cases",
                "/api/review-cases/" + Guid.NewGuid().ToString("D") + "/resolve",
                "/api/review-cases/" + Guid.NewGuid().ToString("D") + "/dismiss",
            })
            {
                await using IAPIResponse excluded = await page.APIRequest.PostAsync(
                    browserOrigin + path,
                    new()
                    {
                        Headers = new Dictionary<string, string>
                        {
                            ["Content-Type"] = "application/json",
                            ["X-CSRF-Token"] = csrf ?? string.Empty,
                            ["Idempotency-Key"] = Guid.NewGuid().ToString("D"),
                        },
                        Data = "{}",
                    });
                Assert.True(
                    path.Contains("/imports/", StringComparison.Ordinal)
                        ? excluded.Status is 403 or 415
                        : excluded.Status == 403,
                    path + " returned " + excluded.Status);
            }

            Assert.Equal(0, await reconciliation.ImportBatches.CountAsync());
        }
        catch (Exception exception)
        {
            api ??= exception is E2EApiStartupException startup ? startup.Api : null;
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

            bool apiStopped = api is null;
            if (api is not null)
            {
                try
                {
                    await api.DisposeAsync();
                }
                catch (Exception exception)
                {
                    testFailure = testFailure is null
                        ? exception
                        : new AggregateException(testFailure, exception);
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

    [Fact]
    public async Task Operator_allocates_and_reversal_approver_reverses_once()
    {
        await using BrowserRun run = await BrowserRun.StartAsync(reversalApprover: true);
        await run.Page.GotoAsync(run.BrowserOrigin);
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history" }).ClickAsync();
        await run.Page.GetByLabel("Invoice").FillAsync(run.InvoiceId.ToString("D"));
        await run.Page.GetByLabel("Bank transaction").FillAsync(run.BankTransactionId.ToString("D"));
        await run.Page.GetByRole(AriaRole.Textbox, new() { Name = "Amount", Exact = true }).FillAsync("12.50");
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Allocate an amount" }).ClickAsync();
        await run.Page.GetByText("Allocation recorded.").WaitForAsync();
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Reverse allocation" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Dialog).GetByLabel("Reason").FillAsync("duplicate receipt");
        Task<IResponse> reversalTask = run.Page.WaitForResponseAsync(
            response => response.Url.Contains("/reverse", StringComparison.Ordinal) && response.Request.Method == "POST");
        await run.Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Reverse allocation" }).ClickAsync();
        IResponse reversal = await reversalTask;
        string reversalBody = await reversal.TextAsync();
        Assert.True(reversal.Status == 200, reversal.Status + " " + reversalBody);
        Assert.Contains("\"replayed\":false", reversalBody, StringComparison.Ordinal);
        await run.Page.GetByText("Reversal recorded.").WaitForAsync();

        await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(run.ConnectionString);
        Assert.Equal(1, await reconciliation.Allocations.CountAsync());
        Assert.Equal(1, await reconciliation.Allocations.CountAsync(item => item.Status == AllocationStatus.Reversed));
        List<FinancialAuditEntry> audits = await reconciliation.FinancialAuditEntries.AsNoTracking().ToListAsync();
        Assert.Equal(1, audits.Count(item => item.Action == FinancialAuditAction.Reversal));
        Assert.Contains(audits, item => item.Action == FinancialAuditAction.Reversal && item.ActorId == run.UserId.ToString("D"));
    }

    [Fact]
    public async Task Lost_confirmation_response_keeps_the_original_key_after_reload()
    {
        await using BrowserRun run = await BrowserRun.StartAsync(reversalApprover: false);
        string? key = null;
        byte[]? body = null;
        await run.Page.RouteAsync("**/api/matches/confirm", async route =>
        {
            if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await route.FallbackAsync();
                return;
            }

            key = route.Request.Headers["idempotency-key"];
            body = route.Request.PostDataBuffer;
            IAPIResponse fetched = await route.FetchAsync();
            Assert.Equal(201, fetched.Status);
            await using ReconciliationDbContext committed = E2EDatabase.CreateContext(run.ConnectionString);
            Assert.Equal(1, await committed.Allocations.CountAsync());
            await route.AbortAsync();
        });
        await run.Page.GotoAsync(run.BrowserOrigin);
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
        await run.Page.GetByText("The outcome is uncertain.").WaitForAsync();
        await run.Page.UnrouteAsync("**/api/matches/confirm");
        await run.Page.ReloadAsync();
        ILocator organization = run.Page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" });
        if (await organization.CountAsync() > 0)
        {
            await organization.ClickAsync();
        }

        await run.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation" }).ClickAsync();
        Task<IResponse> replayTask = run.Page.WaitForResponseAsync(
            response => response.Url.Contains("/api/matches/confirm", StringComparison.Ordinal) && response.Request.Method == "POST");
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same request again" }).ClickAsync();
        IResponse replay = await replayTask;
        string replayBody = await replay.TextAsync();
        Assert.Equal(key, replay.Request.Headers["idempotency-key"]);
        Assert.True(body is not null && body.AsSpan().SequenceEqual(replay.Request.PostDataBuffer));
        Assert.Equal(200, replay.Status);
        Assert.Contains("\"replayed\":true", replayBody, StringComparison.Ordinal);

        await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(run.ConnectionString);
        Assert.Equal(1, await reconciliation.Allocations.CountAsync());
        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.NotNull(body);
    }

    [Fact]
    public async Task Lost_reversal_response_keeps_the_original_key_after_reload()
    {
        await using BrowserRun run = await BrowserRun.StartAsync(reversalApprover: true);
        await SignInAsync(run);
        await AllocateAsync(run);
        string? key = null;
        byte[]? body = null;
        await run.Page.RouteAsync("**/api/allocations/**/reverse", async route =>
        {
            if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await route.FallbackAsync();
                return;
            }

            key = route.Request.Headers["idempotency-key"];
            body = route.Request.PostDataBuffer;
            IAPIResponse fetched = await route.FetchAsync();
            Assert.Equal(200, fetched.Status);
            await using ReconciliationDbContext committed = E2EDatabase.CreateContext(run.ConnectionString);
            Assert.Equal(1, await committed.Allocations.CountAsync(item => item.Status == AllocationStatus.Reversed));
            Assert.Equal(1, await committed.FinancialAuditEntries.CountAsync(item => item.Action == FinancialAuditAction.Reversal));
            await route.AbortAsync();
        });
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Reverse allocation" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Dialog).GetByLabel("Reason").FillAsync("duplicate receipt");
        await run.Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Reverse allocation" }).ClickAsync();
        await run.Page.GetByText("The outcome is uncertain.").WaitForAsync();
        await run.Page.UnrouteAsync("**/api/allocations/**/reverse");
        await run.Page.ReloadAsync();
        await SelectOrganizationIfPresentAsync(run, "Select North Ledger");
        await run.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history" }).ClickAsync();
        await run.Page.GetByText("Reversed").WaitForAsync();
        Task<IResponse> replayTask = run.Page.WaitForResponseAsync(
            response => response.Url.Contains("/reverse", StringComparison.Ordinal) && response.Request.Method == "POST");
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same request again" }).ClickAsync();
        IResponse replay = await replayTask;
        Assert.Equal(key, replay.Request.Headers["idempotency-key"]);
        Assert.True(body is not null && body.AsSpan().SequenceEqual(replay.Request.PostDataBuffer));
        Assert.Equal(200, replay.Status);
        Assert.Contains("\"replayed\":true", await replay.TextAsync(), StringComparison.Ordinal);
        await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(run.ConnectionString);
        Assert.Equal(1, await reconciliation.Allocations.CountAsync(item => item.Status == AllocationStatus.Reversed));
        Assert.Equal(1, await reconciliation.FinancialAuditEntries.CountAsync(item => item.Action == FinancialAuditAction.Reversal));
    }

    [Fact]
    public async Task Uncertain_descriptor_is_not_posted_after_an_organization_switch()
    {
        await using BrowserRun run = await BrowserRun.StartAsync(BrowserSeed.OperatorWithSecondOrganization);
        string? key = null;
        int posts = 0;
        await run.Page.RouteAsync("**/api/matches/confirm", async route =>
        {
            if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await route.FallbackAsync();
                return;
            }

            posts++;
            if (posts == 1)
            {
                key = route.Request.Headers["idempotency-key"];
                IAPIResponse fetched = await route.FetchAsync();
                Assert.Equal(201, fetched.Status);
                await route.AbortAsync();
                return;
            }

            await route.AbortAsync();
        });
        await SignInAsync(run);
        await run.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
        await run.Page.GetByText("The outcome is uncertain.").WaitForAsync();
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Switch to South Ledger" }).ClickAsync();
        await run.Page.GetByText("Active organization: South Ledger").WaitForAsync();
        Assert.Equal(1, posts);
        Assert.Equal(0, await run.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same request again" }).CountAsync());
        string? stored = await run.Page.EvaluateAsync<string?>("() => sessionStorage.getItem('finopsdesk.cookie-recovery.confirm')");
        string capturedKey = key ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(capturedKey));
        Assert.Contains(capturedKey, stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Role_revocation_after_an_uncertain_post_disables_retry()
    {
        await using BrowserRun run = await BrowserRun.StartAsync(reversalApprover: false);
        string? key = null;
        await run.Page.RouteAsync("**/api/matches/confirm", async route =>
        {
            if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await route.FallbackAsync();
                return;
            }

            key = route.Request.Headers["idempotency-key"];
            IAPIResponse fetched = await route.FetchAsync();
            Assert.Equal(201, fetched.Status);
            await using ReconciliationDbContext committed = E2EDatabase.CreateContext(run.ConnectionString);
            Assert.Equal(1, await committed.Allocations.CountAsync());
            await route.AbortAsync();
        });
        await SignInAsync(run);
        await run.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
        await run.Page.GetByText("The outcome is uncertain.").WaitForAsync();
        await run.Page.UnrouteAsync("**/api/matches/confirm");
        await using (AccessDbContext access = E2EDatabase.CreateAccessContext(run.ConnectionString))
        {
            OrganizationRoleAssignment assignment = await access.OrganizationRoleAssignments.SingleAsync(
                item => item.Role == OrganizationRole.Operator);
            assignment.Revoke(DateTimeOffset.UtcNow, RoleActor.DeploymentBootstrap("e2e-revoke"));
            await access.SaveChangesAsync();
        }

        await run.Page.ReloadAsync();
        await SelectOrganizationIfPresentAsync(run, "Select North Ledger");
        await run.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation" }).ClickAsync();
        await run.Page.GetByText("was not rolled back").WaitForAsync();
        Assert.Equal(0, await run.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same request again" }).CountAsync());
        Assert.Equal(0, await run.Page.GetByText("The financial request was not sent.").CountAsync());
        string? stored = await run.Page.EvaluateAsync<string?>("() => sessionStorage.getItem('finopsdesk.cookie-recovery.confirm')");
        string capturedKey = key ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(capturedKey));
        Assert.Contains(capturedKey, stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Operator_cannot_reverse_and_reversal_approver_has_no_operator_controls()
    {
        await using (BrowserRun operatorRun = await BrowserRun.StartAsync(reversalApprover: false))
        {
            await SignInAsync(operatorRun);
            await AllocateAsync(operatorRun);
            Assert.Equal(0, await operatorRun.Page.GetByRole(AriaRole.Button, new() { Name = "Reverse allocation" }).CountAsync());
        }

        await using BrowserRun approverRun = await BrowserRun.StartAsync(BrowserSeed.ReversalApprover);
        await SignInAsync(approverRun);
        await approverRun.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation" }).ClickAsync();
        Assert.Equal(0, await approverRun.Page.GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).CountAsync());
        await approverRun.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history" }).ClickAsync();
        Assert.Equal(0, await approverRun.Page.GetByRole(AriaRole.Button, new() { Name = "Allocate an amount" }).CountAsync());
    }

    private static async Task SignInAsync(BrowserRun run)
    {
        await run.Page.GotoAsync(run.BrowserOrigin);
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).ClickAsync();
    }

    private static async Task SelectOrganizationIfPresentAsync(BrowserRun run, string name)
    {
        ILocator organization = run.Page.GetByRole(AriaRole.Button, new() { Name = name });
        if (await organization.CountAsync() > 0)
        {
            await organization.ClickAsync();
        }
    }

    private static async Task AllocateAsync(BrowserRun run)
    {
        await run.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history" }).ClickAsync();
        await run.Page.GetByLabel("Invoice").FillAsync(run.InvoiceId.ToString("D"));
        await run.Page.GetByLabel("Bank transaction").FillAsync(run.BankTransactionId.ToString("D"));
        await run.Page.GetByRole(AriaRole.Textbox, new() { Name = "Amount", Exact = true }).FillAsync("12.50");
        await run.Page.GetByRole(AriaRole.Button, new() { Name = "Allocate an amount" }).ClickAsync();
        await run.Page.GetByText("Allocation recorded.").WaitForAsync();
    }

    private static async Task SeedOperatorPairAsync(string connectionString, string issuer)
    {
        _ = await SeedPairAsync(connectionString, issuer, BrowserSeed.Operator);
    }

    private static async Task<SeededPair> SeedPairAsync(string connectionString, string issuer, BrowserSeed reversalApprover)
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
            if (reversalApprover != BrowserSeed.ReversalApprover)
            {
                access.OrganizationRoleAssignments.Add(OrganizationRoleAssignment.Create(
                    Guid.NewGuid(),
                    membershipId,
                    OrganizationRole.Viewer,
                    createdAt,
                    RoleActor.DeploymentBootstrap("e2e-viewer")));
                access.OrganizationRoleAssignments.Add(OrganizationRoleAssignment.Create(
                    Guid.NewGuid(),
                    membershipId,
                    OrganizationRole.Operator,
                    createdAt.AddSeconds(1),
                    RoleActor.DeploymentBootstrap("e2e-operator")));
            }

            if (reversalApprover == BrowserSeed.OperatorAndReversalApprover || reversalApprover == BrowserSeed.ReversalApprover)
            {
                access.OrganizationRoleAssignments.Add(OrganizationRoleAssignment.Create(
                    Guid.NewGuid(),
                    membershipId,
                    OrganizationRole.ReversalApprover,
                    createdAt.AddSeconds(2),
                    RoleActor.DeploymentBootstrap("e2e-reversal")));
            }

            if (reversalApprover == BrowserSeed.OperatorWithSecondOrganization)
            {
                Guid southId = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
                access.Organizations.Add(Organization.Create(southId, "South Ledger", createdAt));
                access.OrganizationMemberships.Add(OrganizationMembership.Grant(
                    Guid.NewGuid(),
                    userId,
                    southId,
                    createdAt));
            }

            await access.SaveChangesAsync();
        }

        OrganizationId organization = new(OrganizationId);
        CurrencyCode pln = new("PLN");
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        await using ReconciliationDbContext reconciliation = E2EDatabase.CreateContext(connectionString);
        reconciliation.Invoices.Add(new Invoice(
            invoiceId,
            organization,
            "COOKIE-WRITE-1",
            new Money(12.50m, pln)));
        reconciliation.BankTransactions.Add(new BankTransaction(
            bankTransactionId,
            organization,
            new Money(12.50m, pln),
            new DateOnly(2026, 8, 24),
            "COOKIE-WRITE-1"));
        await reconciliation.SaveChangesAsync();
        return new SeededPair(userId, invoiceId.Value, bankTransactionId.Value);
    }

    private enum BrowserSeed
    {
        Operator,
        OperatorAndReversalApprover,
        ReversalApprover,
        OperatorWithSecondOrganization,
    }

    private sealed class BrowserRun : IAsyncDisposable
    {
        private readonly string _certificatePath;
        private readonly E2EDatabase _database;
        private readonly OidcProtocolStub _stub;
        private readonly E2EApiProcess _api;
        private readonly E2EViteProcess _vite;
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;

        private BrowserRun(
            string certificatePath,
            E2EDatabase database,
            OidcProtocolStub stub,
            E2EApiProcess api,
            E2EViteProcess vite,
            IPlaywright playwright,
            IBrowser browser,
            IPage page,
            SeededPair pair,
            string browserOrigin)
        {
            _certificatePath = certificatePath;
            _database = database;
            _stub = stub;
            _api = api;
            _vite = vite;
            _playwright = playwright;
            _browser = browser;
            Page = page;
            UserId = pair.UserId;
            InvoiceId = pair.InvoiceId;
            BankTransactionId = pair.BankTransactionId;
            ConnectionString = database.ConnectionString;
            BrowserOrigin = browserOrigin;
        }

        public IPage Page { get; }

        public Guid UserId { get; }

        public Guid InvoiceId { get; }

        public Guid BankTransactionId { get; }

        public string ConnectionString { get; }

        public string BrowserOrigin { get; }

        public static Task<BrowserRun> StartAsync(bool reversalApprover)
        {
            return StartAsync(reversalApprover ? BrowserSeed.OperatorAndReversalApprover : BrowserSeed.Operator);
        }

        public static async Task<BrowserRun> StartAsync(BrowserSeed seed)
        {
            string certificatePath = Path.Combine(Path.GetTempPath(), "finopsdesk-cookie-write-" + Guid.NewGuid().ToString("N") + ".crt");
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
                    CancellationToken.None,
                    financialWriteAuthorizationEnabled: true);
                SeededPair pair = await SeedPairAsync(database.ConnectionString, stub.Authority, seed);
                vite = await E2EViteProcess.StartAsync(
                    api.Origin,
                    new E2EViteLaunch { Host = "localhost", Port = browserPort, CookieMode = true, NodeExtraCaCerts = certificatePath },
                    CancellationToken.None);
                playwright = await Playwright.CreateAsync();
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Timeout = 30_000 });
                IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = false, BaseURL = browserOrigin });
                IPage page = await context.NewPageAsync();
                page.SetDefaultTimeout(60_000);
                return new BrowserRun(certificatePath, database, stub, api, vite, playwright, browser, page, pair, browserOrigin);
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

        public async ValueTask DisposeAsync()
        {
            await _browser.CloseAsync();
            _playwright.Dispose();
            await _vite.DisposeAsync();
            await _api.DisposeAsync();
            await _stub.DisposeAsync();
            await _database.DisposeAsync();
            if (File.Exists(_certificatePath))
            {
                File.Delete(_certificatePath);
            }
        }
    }

    private readonly record struct SeededPair(Guid UserId, Guid InvoiceId, Guid BankTransactionId);
}