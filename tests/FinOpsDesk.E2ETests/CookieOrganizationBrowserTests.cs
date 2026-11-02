using System.Text.Json;

using FinOpsDesk.IntegrationTests.Api;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Access.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class CookieOrganizationBrowserTests
{
    private static readonly Guid NorthId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static readonly Guid SouthId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Browser_selects_an_organization_only_after_the_server_confirms_it()
    {
        await using CookieBrowserHost host = await CookieBrowserHost.StartAsync();
        IBrowserContext context = await host.NewContextAsync();
        IPage page = await context.NewPageAsync();
        int bearerCount = 0;
        int organizationPosts = 0;
        bool organizationPostHasCsrf = false;
        bool organizationPostHasBearer = false;
        page.Request += (_, request) =>
        {
            if (HasBearer(request))
            {
                bearerCount++;
            }

            if (request.Method == "POST" && request.Url.Contains("/api/session/organization", StringComparison.Ordinal))
            {
                organizationPosts++;
                organizationPostHasBearer = HasBearer(request);
                organizationPostHasCsrf = HasHeader(request, "x-csrf-token");
            }
        };

        await SignInAsync(host, page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Select South Ledger" }).WaitForAsync();
        Assert.Equal(0, await page.GetByText("Active organization:", new() { Exact = false }).CountAsync());

        JsonElement before = await ReadOrganizationProbeAsync(page);
        Assert.Equal(2, before.GetProperty("organizationCount").GetInt32());
        Assert.True(before.GetProperty("activeOrganizationId").ValueKind == JsonValueKind.Null);
        string cookieBefore = await ReadSessionCookieValueAsync(context, host.Origin);

        await page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).ClickAsync();
        await page.GetByText("Active organization: North Ledger").WaitForAsync();
        JsonElement north = await ReadOrganizationProbeAsync(page);
        Assert.Equal(NorthId.ToString("D"), north.GetProperty("activeOrganizationId").GetString());
        Assert.Equal("North Ledger", north.GetProperty("activeName").GetString());
        Assert.False(north.GetProperty("financialAccess").GetBoolean());
        Assert.Equal(1, organizationPosts);
        Assert.True(organizationPostHasCsrf);
        Assert.False(organizationPostHasBearer);
        Assert.False(
            string.Equals(cookieBefore, await ReadSessionCookieValueAsync(context, host.Origin), StringComparison.Ordinal),
            "The organization selection did not replace the session cookie.");

        await page.GetByRole(AriaRole.Button, new() { Name = "Switch to South Ledger" }).ClickAsync();
        await page.GetByText("Active organization: South Ledger").WaitForAsync();
        JsonElement south = await ReadOrganizationProbeAsync(page);
        Assert.Equal(SouthId.ToString("D"), south.GetProperty("activeOrganizationId").GetString());
        Assert.Equal(0, bearerCount);
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Invoices" }).CountAsync());

        await using (IAPIResponse forbidden = await page.APIRequest.GetAsync(host.Origin + "/api/invoices"))
        {
            Assert.Equal(403, forbidden.Status);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).WaitForAsync();
        Assert.Equal(0, await page.GetByText("North Ledger").CountAsync());
        Assert.Equal(0, await page.GetByText("South Ledger").CountAsync());
        Assert.Equal(0, await page.GetByText("Active organization:", new() { Exact = false }).CountAsync());
        JsonElement signedOut = await ReadOrganizationProbeAsync(page);
        Assert.False(signedOut.GetProperty("authenticated").GetBoolean());
        Assert.Equal(0, bearerCount);
    }

    [Fact]
    public async Task Second_tab_shows_the_organization_confirmed_by_the_server()
    {
        await using CookieBrowserHost host = await CookieBrowserHost.StartAsync();
        IBrowserContext context = await host.NewContextAsync();
        IPage first = await context.NewPageAsync();
        await SignInAsync(host, first);
        await first.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).WaitForAsync();

        IPage second = await context.NewPageAsync();
        await second.GotoAsync(host.Origin);
        await second.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).WaitForAsync();

        int bearerCount = 0;
        first.Request += (_, request) =>
        {
            if (HasBearer(request))
            {
                bearerCount++;
            }
        };
        second.Request += (_, request) =>
        {
            if (HasBearer(request))
            {
                bearerCount++;
            }
        };

        await first.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).ClickAsync();
        await first.GetByText("Active organization: North Ledger").WaitForAsync();
        await second.GetByText("Active organization: North Ledger").WaitForAsync();
        await first.GetByRole(AriaRole.Button, new() { Name = "Switch to South Ledger" }).ClickAsync();
        await first.GetByText("Active organization: South Ledger").WaitForAsync();
        await second.GetByText("Active organization: South Ledger").WaitForAsync();
        Assert.Equal(0, await first.GetByText("Active organization: North Ledger").CountAsync());
        Assert.Equal(0, await second.GetByText("Active organization: North Ledger").CountAsync());
        Assert.Equal(0, await first.GetByRole(AriaRole.Button, new() { Name = "Invoices" }).CountAsync());
        Assert.Equal(0, await second.GetByRole(AriaRole.Button, new() { Name = "Invoices" }).CountAsync());
        JsonElement confirmed = await ReadOrganizationProbeAsync(second);
        Assert.Equal(SouthId.ToString("D"), confirmed.GetProperty("activeOrganizationId").GetString());
        Assert.False(confirmed.GetProperty("financialAccess").GetBoolean());
        Assert.Equal(0, bearerCount);
    }

    [Fact]
    public async Task Browser_drops_a_revoked_membership_after_reload()
    {
        await using CookieBrowserHost host = await CookieBrowserHost.StartAsync();
        IBrowserContext context = await host.NewContextAsync();
        IPage page = await context.NewPageAsync();
        await SignInAsync(host, page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).ClickAsync();
        await page.GetByText("Active organization: North Ledger").WaitForAsync();

        await host.RevokeNorthAsync();
        await page.ReloadAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Select South Ledger" }).WaitForAsync();
        Assert.Equal(0, await page.GetByText("North Ledger").CountAsync());
        Assert.Equal(0, await page.GetByText("Active organization:", new() { Exact = false }).CountAsync());
        JsonElement probe = await ReadOrganizationProbeAsync(page);
        Assert.Equal(1, probe.GetProperty("organizationCount").GetInt32());
        Assert.True(probe.GetProperty("activeOrganizationId").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Delayed_organization_cookie_cannot_restore_a_revoked_session()
    {
        await using CookieBrowserHost host = await CookieBrowserHost.StartAsync();
        IBrowserContext context = await host.NewContextAsync();
        IPage first = await context.NewPageAsync();
        int bearerCount = 0;
        first.Request += (_, request) =>
        {
            if (HasBearer(request))
            {
                bearerCount++;
            }
        };
        await SignInAsync(host, first);
        await first.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" }).WaitForAsync();
        (int total, int revoked) signedIn = await host.CountSessionsAsync();
        Assert.Equal(1, signedIn.total);
        Assert.Equal(0, signedIn.revoked);

        TaskCompletionSource<IAPIResponse> captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await first.RouteAsync("**/api/session/organization", async route =>
        {
            IAPIResponse response = await route.FetchAsync(new RouteFetchOptions
            {
                MaxRetries = 0,
                MaxRedirects = 0,
                Timeout = 20_000,
            });
            captured.TrySetResult(response);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await route.FulfillAsync(new RouteFulfillOptions { Response = response });
        });

        await first.GetByRole(AriaRole.Button, new() { Name = "Select North Ledger" })
            .EvaluateAsync("button => button.click()");
        IAPIResponse held = await captured.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(204, held.Status);
        (int total, int revoked) selected = await host.CountSessionsAsync();
        Assert.Equal(1, selected.total);
        Assert.Equal(0, selected.revoked);

        IPage second = await context.NewPageAsync();
        second.Request += (_, request) =>
        {
            if (HasBearer(request))
            {
                bearerCount++;
            }
        };
        await second.GotoAsync(host.Origin);
        await second.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await second.GetByText("FinOpsDesk signed out in this browser.").WaitForAsync();
        (int total, int revoked) signedOut = await host.CountSessionsAsync();
        Assert.Equal(1, signedOut.total);
        Assert.Equal(1, signedOut.revoked);

        release.TrySetResult(true);
        await first.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).WaitForAsync();
        JsonElement probe = await ReadOrganizationProbeAsync(first);
        Assert.Equal(200, probe.GetProperty("status").GetInt32());
        Assert.False(probe.GetProperty("authenticated").GetBoolean());
        Assert.True(probe.GetProperty("activeOrganizationId").ValueKind == JsonValueKind.Null);
        Assert.False(probe.GetProperty("financialAccess").GetBoolean());
        Assert.Equal(0, await first.GetByRole(AriaRole.Button, new() { Name = "Invoices" }).CountAsync());
        Assert.Equal(0, bearerCount);
        (int total, int revoked) after = await host.CountSessionsAsync();
        Assert.Equal(1, after.total);
        Assert.Equal(1, after.revoked);
    }

    private static async Task SignInAsync(CookieBrowserHost host, IPage page)
    {
        await page.GotoAsync(host.Origin);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Signed in" }).WaitForAsync();
    }

    private static async Task<JsonElement> ReadOrganizationProbeAsync(IPage page)
    {
        string json = await page.EvaluateAsync<string>(
            """
            async () => {
              const response = await fetch('/api/session', {
                credentials: 'same-origin',
                cache: 'no-store',
                headers: { Accept: 'application/json' }
              });
              const body = await response.json();
              const active = body.activeOrganization;
              return JSON.stringify({
                status: response.status,
                authenticated: body.authenticated === true,
                financialAccess: body.financialAccess === true,
                organizationCount: Array.isArray(body.organizations) ? body.organizations.length : -1,
                activeOrganizationId: active && typeof active.organizationId === 'string' ? active.organizationId : null,
                activeName: active && typeof active.displayName === 'string' ? active.displayName : null
              });
            }
            """);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<string> ReadSessionCookieValueAsync(IBrowserContext context, string origin)
    {
        IReadOnlyList<BrowserContextCookiesResult> cookies = await context.CookiesAsync(origin);
        foreach (BrowserContextCookiesResult cookie in cookies)
        {
            if (cookie.Name == "__Host-finopsdesk-session")
            {
                return cookie.Value;
            }
        }

        throw new InvalidOperationException("The session cookie was not stored.");
    }

    private static bool HasBearer(IRequest request)
    {
        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            if (header.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                && header.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasHeader(IRequest request, string name)
    {
        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                && header.Value.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CookieBrowserHost : IAsyncDisposable
    {
        private readonly string? _certificatePath;
        private readonly OidcProtocolStub _stub;
        private readonly E2EApiProcess _api;
        private readonly E2EViteProcess _vite;
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly Guid _northMembershipId;
        private bool _disposed;

        private CookieBrowserHost(
            E2EDatabase database,
            string? certificatePath,
            OidcProtocolStub stub,
            E2EApiProcess api,
            E2EViteProcess vite,
            IPlaywright playwright,
            IBrowser browser,
            string origin,
            Guid northMembershipId)
        {
            Database = database;
            _certificatePath = certificatePath;
            _stub = stub;
            _api = api;
            _vite = vite;
            _playwright = playwright;
            _browser = browser;
            Origin = origin;
            _northMembershipId = northMembershipId;
        }

        public E2EDatabase Database { get; }

        public string Origin { get; }

        public static async Task<CookieBrowserHost> StartAsync()
        {
            string certificatePath = Path.Combine(Path.GetTempPath(), "finopsdesk-cookie-org-" + Guid.NewGuid().ToString("N") + ".crt");
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
                string origin = "http://localhost:" + browserPort;
                stub = await OidcProtocolStub.StartAsync(origin + "/signin-oidc");
                api = await E2EApiProcess.StartCookieAsync(
                    database.ConnectionString,
                    stub.Authority,
                    origin,
                    OidcProtocolStub.ClientId,
                    OidcProtocolStub.ClientSecret,
                    CancellationToken.None);
                Guid northMembershipId = await SeedMembershipsAsync(database.ConnectionString, stub.Authority);
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
                return new CookieBrowserHost(
                    database,
                    certificatePath,
                    stub,
                    api,
                    vite,
                    playwright,
                    browser,
                    origin,
                    northMembershipId);
            }
            catch (Exception exception)
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
                    if (api.TerminationEstablished && database is not null)
                    {
                        await database.DisposeAsync();
                    }
                }
                else if (database is not null)
                {
                    await database.DisposeAsync();
                }

                if (stub is not null)
                {
                    await stub.DisposeAsync();
                }

                if (File.Exists(certificatePath))
                {
                    File.Delete(certificatePath);
                }

                if (exception is PlaywrightException)
                {
                    throw new InvalidOperationException(
                        "Chromium is not available. Build the E2E project, then run playwright.ps1 install chromium from its output directory.",
                        exception);
                }

                throw;
            }
        }

        public async Task<IBrowserContext> NewContextAsync()
        {
            IBrowserContext context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = false,
                BaseURL = Origin,
            });
            context.SetDefaultTimeout(60_000);
            return context;
        }

        public async Task RevokeNorthAsync()
        {
            await using AccessDbContext access = E2EDatabase.CreateAccessContext(Database.ConnectionString);
            OrganizationMembership membership = await access.OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == _northMembershipId);
            membership.Revoke(DateTimeOffset.UtcNow);
            await access.SaveChangesAsync();
        }

        public async Task<(int Total, int Revoked)> CountSessionsAsync()
        {
            await using AccessDbContext access = E2EDatabase.CreateAccessContext(Database.ConnectionString);
            int total = await access.BrowserSessions.CountAsync();
            int revoked = await access.BrowserSessions.CountAsync(session => session.RevokedAt != null);
            return (total, revoked);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Exception? cleanupFailure = null;
            try
            {
                await _browser.CloseAsync();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            _playwright.Dispose();
            try
            {
                await _vite.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }

            try
            {
                await _api.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }

            try
            {
                await _stub.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }

            if (_api.TerminationEstablished)
            {
                await Database.DisposeAsync();
            }
            else
            {
                throw new InvalidOperationException(
                    "The cookie organization API was not confirmed stopped, so database "
                    + Database.DatabaseName
                    + " was left in place.",
                    cleanupFailure);
            }

            if (_certificatePath is not null && File.Exists(_certificatePath))
            {
                File.Delete(_certificatePath);
            }

            if (cleanupFailure is not null)
            {
                throw cleanupFailure;
            }
        }

        private static async Task<Guid> SeedMembershipsAsync(string connectionString, string issuer)
        {
            await using AccessDbContext access = E2EDatabase.CreateAccessContext(connectionString);
            DateTimeOffset createdAt = DateTimeOffset.UtcNow;
            Guid userId = Guid.NewGuid();
            Guid northMembershipId = Guid.NewGuid();
            access.Organizations.Add(Organization.Create(NorthId, "North Ledger", createdAt));
            access.Organizations.Add(Organization.Create(SouthId, "South Ledger", createdAt));
            access.ApplicationUsers.Add(ApplicationUser.Create(
                userId,
                issuer,
                OidcProtocolStub.Subject,
                OidcProtocolStub.DisplayName,
                createdAt));
            access.OrganizationMemberships.Add(OrganizationMembership.Grant(northMembershipId, userId, NorthId, createdAt));
            access.OrganizationMemberships.Add(OrganizationMembership.Grant(Guid.NewGuid(), userId, SouthId, createdAt));
            await access.SaveChangesAsync();
            return northMembershipId;
        }
    }
}