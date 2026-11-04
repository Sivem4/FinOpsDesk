using System.Text.Json;

using FinOpsDesk.IntegrationTests.Api;

using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class CookieOidcBrowserTests
{
    [Fact]
    public async Task Browser_completes_cookie_sign_in_and_local_sign_out()
    {
        string? certificatePath = null;
        E2EDatabase? database = null;
        OidcProtocolStub? stub = null;
        E2EApiProcess? api = null;
        E2EViteProcess? vite = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        bool apiStopped = false;
        Exception? testFailure = null;

        try
        {
            certificatePath = Path.Combine(Path.GetTempPath(), "finopsdesk-cookie-smoke-" + Guid.NewGuid().ToString("N") + ".crt");
            DevelopmentCertificateExport.WritePublicPem(certificatePath);
            database = await E2EDatabase.CreateAsync(CancellationToken.None);
            int browserPort = LoopbackPort.Reserve();
            string browserOrigin = "http://localhost:" + browserPort;
            stub = await OidcProtocolStub.StartAsync(browserOrigin + "/signin-oidc");
            string authority = stub.Authority;
            api = await E2EApiProcess.StartCookieAsync(
                database.ConnectionString,
                stub.Authority,
                browserOrigin,
                OidcProtocolStub.ClientId,
                OidcProtocolStub.ClientSecret,
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
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Timeout = 30_000,
                });
            }
            catch (Exception exception)
            {
                throw E2EChromium.Unavailable(exception);
            }

            IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = false,
                BaseURL = browserOrigin,
            });
            IPage page = await context.NewPageAsync();
            page.SetDefaultTimeout(60_000);

            bool sawLoginPost = false;
            bool loginHasCsrfField = false;
            bool loginHasReturnUrl = false;
            bool sawAuthorize = false;
            bool authorizeHasPkce = false;
            bool sawCallback = false;
            int bearerCount = 0;
            page.Request += (_, request) =>
            {
                if (HasBearer(request))
                {
                    bearerCount++;
                }

                if (request.Method == "POST"
                    && request.Url.Contains("/login", StringComparison.OrdinalIgnoreCase))
                {
                    sawLoginPost = true;
                    string body = request.PostData ?? string.Empty;
                    loginHasCsrfField = body.Contains("__RequestVerificationToken=", StringComparison.Ordinal);
                    loginHasReturnUrl = body.Contains("returnUrl=", StringComparison.Ordinal);
                }

                if (request.Url.StartsWith(authority + "/authorize", StringComparison.Ordinal))
                {
                    sawAuthorize = true;
                    authorizeHasPkce = request.Url.Contains("code_challenge=", StringComparison.Ordinal)
                        && request.Url.Contains("code_challenge_method=S256", StringComparison.Ordinal);
                }

                if (request.Url.Contains("/signin-oidc", StringComparison.Ordinal)
                    && request.Url.Contains("code=", StringComparison.Ordinal))
                {
                    sawCallback = true;
                }
            };

            await page.GotoAsync(browserOrigin);
            await RequireVisibleAsync(
                page,
                page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }),
                api,
                vite,
                "The sign-in form");
            JsonElement anonymous = await ReadSessionProbeAsync(page);
            Assert.Equal(200, anonymous.GetProperty("status").GetInt32());
            Assert.False(anonymous.GetProperty("authenticated").GetBoolean());
            Assert.False(anonymous.GetProperty("financialAccess").GetBoolean());
            Assert.True(anonymous.GetProperty("hasCsrf").GetBoolean());
            Assert.False(anonymous.GetProperty("hasIssuer").GetBoolean());

            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
            try
            {
                await page.GetByRole(AriaRole.Heading, new() { Name = "Signed in" }).WaitForAsync();
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    "The restricted screen was not shown. url=" + SafeUrl(page.Url)
                    + " loginPost=" + sawLoginPost
                    + " authorize=" + sawAuthorize
                    + " callback=" + sawCallback
                    + Environment.NewLine
                    + api.Diagnostics()
                    + Environment.NewLine
                    + vite.Diagnostics(),
                    exception);
            }

            Assert.True(sawLoginPost);
            Assert.True(loginHasCsrfField);
            Assert.True(loginHasReturnUrl);
            Assert.True(sawAuthorize);
            Assert.True(authorizeHasPkce);
            Assert.True(sawCallback);
            Assert.Equal(0, bearerCount);
            await AssertSessionCookieAsync(context, browserOrigin);
            await AssertNoSecretAsync(page);

            JsonElement signedIn = await ReadSessionProbeAsync(page);
            Assert.Equal(200, signedIn.GetProperty("status").GetInt32());
            Assert.True(signedIn.GetProperty("authenticated").GetBoolean());
            Assert.False(signedIn.GetProperty("financialAccess").GetBoolean());
            Assert.True(signedIn.GetProperty("hasCsrf").GetBoolean());
            Assert.True(signedIn.GetProperty("hasIssuer").GetBoolean());
            Assert.True(signedIn.GetProperty("hasSubject").GetBoolean());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Invoices" }).CountAsync());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Bank transactions" }).CountAsync());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Imports" }).CountAsync());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Reconciliation" }).CountAsync());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Allocation history" }).CountAsync());
            Assert.Equal(0, await page.GetByText("Development access token").CountAsync());

            await using (IAPIResponse forbidden = await page.APIRequest.GetAsync(browserOrigin + "/api/invoices"))
            {
                Assert.Equal(403, forbidden.Status);
            }

            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Signed in" }).WaitForAsync();
            await AssertSessionCookieAsync(context, browserOrigin);

            await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).WaitForAsync();
            await page.GetByText("FinOpsDesk signed out in this browser.").WaitForAsync();
            JsonElement signedOut = await ReadSessionProbeAsync(page);
            Assert.Equal(200, signedOut.GetProperty("status").GetInt32());
            Assert.False(signedOut.GetProperty("authenticated").GetBoolean());
            Assert.False(signedOut.GetProperty("financialAccess").GetBoolean());
            Assert.True(signedOut.GetProperty("hasCsrf").GetBoolean());

            await using (IAPIResponse anonymousInvoices = await page.APIRequest.GetAsync(browserOrigin + "/api/invoices"))
            {
                Assert.Equal(401, anonymousInvoices.Status);
            }
            Assert.Equal(0, bearerCount);
            Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length"));
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

        if (database is not null && !apiStopped)
        {
            throw new InvalidOperationException(
                "The cookie smoke API was not confirmed stopped, so database "
                + database.DatabaseName
                + " was left in place.",
                testFailure);
        }

        if (testFailure is not null)
        {
            throw testFailure;
        }
    }

    private static async Task RequireVisibleAsync(
        IPage page,
        ILocator locator,
        E2EApiProcess api,
        E2EViteProcess vite,
        string step)
    {
        try
        {
            await locator.WaitForAsync();
        }
        catch (TimeoutException exception)
        {
            string text = await page.EvaluateAsync<string>(
                "() => document.body ? document.body.innerText.slice(0, 500) : ''");
            if (text.Contains(OidcProtocolStub.ClientSecret, StringComparison.Ordinal))
            {
                text = "(page text omitted)";
            }

            throw new InvalidOperationException(
                step + " was not shown. url=" + SafeUrl(page.Url) + Environment.NewLine
                + text + Environment.NewLine
                + api.Diagnostics() + Environment.NewLine
                + vite.Diagnostics(),
                exception);
        }
    }

    private static async Task AssertSessionCookieAsync(IBrowserContext context, string browserOrigin)
    {
        IReadOnlyList<BrowserContextCookiesResult> cookies = await context.CookiesAsync(browserOrigin);
        BrowserContextCookiesResult? session = null;
        foreach (BrowserContextCookiesResult cookie in cookies)
        {
            if (cookie.Name == "__Host-finopsdesk-session")
            {
                session = cookie;
                break;
            }
        }

        Assert.NotNull(session);
        Assert.True(session.Secure);
        Assert.True(session.HttpOnly);
        Assert.Equal(SameSiteAttribute.Strict, session.SameSite);
        Assert.Equal("/", session.Path);
        Assert.Equal("localhost", session.Domain);
    }

    private static async Task AssertNoSecretAsync(IPage page)
    {
        string html = await page.ContentAsync();
        bool leaked = html.Contains(OidcProtocolStub.ClientSecret, StringComparison.Ordinal);
        Assert.False(leaked, "The page contained the test client secret.");
    }

    private static async Task<JsonElement> ReadSessionProbeAsync(IPage page)
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
              return JSON.stringify({
                status: response.status,
                authenticated: body.authenticated === true,
                financialAccess: body.financialAccess === true,
                hasCsrf: typeof body.csrfToken === 'string' && body.csrfToken.length > 0,
                hasIssuer: typeof body.issuer === 'string' && body.issuer.length > 0,
                hasSubject: typeof body.subject === 'string' && body.subject.length > 0
              });
            }
            """);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
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

    private static string SafeUrl(string url)
    {
        return url.Contains("code=", StringComparison.Ordinal) ? "(callback URL omitted)" : url;
    }
}