using System.Net;

using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

internal sealed class E2EHost : IAsyncDisposable
{
    private readonly string _signingKey;
    private readonly string _accessToken;
    private readonly E2EDatabase _database;
    private readonly E2EApiProcess _api;
    private readonly E2EViteProcess _vite;
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _pageErrors = [];
    private readonly List<string> _serverErrors = [];
    private readonly TaskCompletionSource _browserClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _cleanupStatus = "not started";
    private int _browserCloseStarted;
    private bool _browserWorkOutstanding;
    private bool _disposed;

    private E2EHost(
        string signingKey,
        string accessToken,
        Guid organizationId,
        E2EDatabase database,
        E2EApiProcess api,
        E2EViteProcess vite,
        IPlaywright playwright,
        IBrowser browser,
        IPage page,
        string fixtureDirectory)
    {
        _signingKey = signingKey;
        _accessToken = accessToken;
        OrganizationId = organizationId;
        _database = database;
        _api = api;
        _vite = vite;
        _playwright = playwright;
        _browser = browser;
        Page = page;
        FixtureDirectory = fixtureDirectory;
    }

    public Guid OrganizationId { get; }

    public IPage Page { get; }

    public string FixtureDirectory { get; }

    public string DatabaseName => _database.DatabaseName;

    public string ConnectionString => _database.ConnectionString;

    internal string CleanupReport => _cleanupStatus;

    internal bool ApiTerminationEstablished => _api.TerminationEstablished;

    internal bool ViteTerminationEstablished => _vite.TerminationEstablished;

    internal int ApiProcessId => _api.ProcessId;

    internal int ViteProcessId => _vite.ProcessId;

    public static async Task<E2EHost> StartAsync(CancellationToken cancellationToken)
    {
        E2EDatabase? database = null;
        E2EApiProcess? api = null;
        E2EViteProcess? vite = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        string? fixtureDirectory = null;
        string? signingKey = null;
        ProcessLifetime apiLifetime = ProcessLifetime.NotStarted;
        ProcessLifetime viteLifetime = ProcessLifetime.NotStarted;

        try
        {
            database = await E2EDatabase.CreateAsync(cancellationToken);
            signingKey = E2EAccessToken.CreateSigningKey();
            try
            {
                api = await E2EApiProcess.StartAsync(database.ConnectionString, signingKey, cancellationToken);
            }
            catch (ProcessStartupException exception)
            {
                apiLifetime = exception.Lifetime;
                throw;
            }

            try
            {
                vite = await E2EViteProcess.StartAsync(api.Origin, cancellationToken);
            }
            catch (ProcessStartupException exception)
            {
                viteLifetime = exception.Lifetime;
                throw;
            }
            Task<IPlaywright> creating = Task.Run(static () => Playwright.CreateAsync());
            playwright = await WithinAsync(creating, "Playwright driver", TimeSpan.FromSeconds(45));
            try
            {
                browser = await WithinAsync(
                    playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = true,
                        Timeout = 30_000,
                    }),
                    "Chromium",
                    TimeSpan.FromSeconds(45));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Chromium is not available. Build the E2E project, then run "
                    + "powershell -File tests/FinOpsDesk.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium. "
                    + SensitiveText.Redact(exception.Message),
                    exception);
            }

            IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = false,
            });
            IPage page = await context.NewPageAsync();
            page.SetDefaultTimeout(20_000);
            page.SetDefaultNavigationTimeout(20_000);
            Assertions.SetDefaultExpectTimeout(15_000);
            fixtureDirectory = Path.Combine(
                Path.GetTempPath(),
                "FinOpsDesk-E2E-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureDirectory);

            Guid organizationId = Guid.NewGuid();
            string accessToken = E2EAccessToken.Create(signingKey, organizationId);
            E2EHost host = new(
                signingKey,
                accessToken,
                organizationId,
                database,
                api,
                vite,
                playwright,
                browser,
                page,
                fixtureDirectory);
            host.Watch(page);
            return host;
        }
        catch (Exception failure)
        {
            string cleanup = await CleanupStartupAsync(
                browser,
                playwright,
                vite,
                viteLifetime,
                api,
                apiLifetime,
                database,
                fixtureDirectory);
            string text = SensitiveText.Redact(
                failure.ToString(),
                signingKey,
                database?.ConnectionString);
            if (cleanup.Length > 0)
            {
                text += Environment.NewLine + SensitiveText.Redact(cleanup, signingKey, database?.ConnectionString);
            }

            throw new InvalidOperationException(text, failure);
        }
    }

    public async Task RunWithinLimitAsync(Func<Task> journey)
    {
        try
        {
            await E2EJourneyLimit.WaitAsync(
                journey(),
                TimeSpan.FromSeconds(150),
                StopBrowserAsync,
                TimeSpan.FromSeconds(20));
        }
        catch (E2EJourneyOutstandingException)
        {
            _browserWorkOutstanding = true;
            throw;
        }
    }

    public async Task SignInAsync()
    {
        await Page.GotoAsync(_vite.Origin + "/imports");
        ILocator tokenField = Page.GetByLabel("Development access token");
        try
        {
            await tokenField.FillAsync(_accessToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "The development token field could not be filled. The token was not written to the log. "
                + Describe(exception.Message));
        }

        await Page.GetByRole(AriaRole.Button, new() { Name = "Use token" }).ClickAsync();
        await Assertions.Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Imports" })).ToBeVisibleAsync();
    }

    public void AssertBrowserIsQuiet()
    {
        if (_pageErrors.Count == 0 && _serverErrors.Count == 0 && _consoleErrors.Count == 0)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException(Describe("Browser reported a fault."));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception> failures = [];
        string browserOutcome = "closed";
        try
        {
            await StopBrowserAsync();
        }
        catch (Exception exception)
        {
            browserOutcome = "failed";
            failures.Add(exception);
        }

        string viteOutcome = await StopViteAsync(failures);
        string apiOutcome = await StopApiAsync(failures);
        E2ECleanupDecision decision = E2ECleanupPolicy.Decide(
            _browserWorkOutstanding,
            _api.TerminationEstablished,
            _vite.TerminationEstablished,
            browserOutcome == "failed");
        string databaseOutcome = await DropOwnedDatabaseAsync(decision, failures);
        string fixtureOutcome = DeleteFixtures(failures);
        _cleanupStatus = string.Join(
            "; ",
            "browser " + browserOutcome,
            "vite " + viteOutcome,
            "api " + apiOutcome,
            "database " + databaseOutcome,
            "fixtures " + fixtureOutcome);

        if (failures.Count == 0 && !decision.FailForProcessLifecycle && fixtureOutcome != "failed")
        {
            return;
        }

        string detail = failures.Count == 0
            ? _cleanupStatus
            : _cleanupStatus + " " + string.Join(" | ", failures.ConvertAll(failure => failure.Message));
        throw new InvalidOperationException(Describe("E2E cleanup failed. " + detail));
    }

    public string Describe(string message)
    {
        string text = string.Join(
            Environment.NewLine,
            message,
            "cleanup: " + _cleanupStatus,
            _api.Diagnostics(),
            _vite.Diagnostics(),
            "console: " + string.Join(" | ", _consoleErrors),
            "pageerror: " + string.Join(" | ", _pageErrors),
            "http5xx: " + string.Join(" | ", _serverErrors));
        return SensitiveText.Redact(text, _signingKey, _accessToken, _database.ConnectionString);
    }

    private async Task StopBrowserAsync()
    {
        if (Interlocked.CompareExchange(ref _browserCloseStarted, 1, 0) != 0)
        {
            await _browserClosed.Task;
            return;
        }

        try
        {
            await _browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(15));
            _playwright.Dispose();
            _browserClosed.TrySetResult();
        }
        catch (Exception exception)
        {
            _browserClosed.TrySetException(exception);
            throw;
        }
    }

    private async Task<string> StopViteAsync(List<Exception> failures)
    {
        try
        {
            await _vite.DisposeAsync();
            return _vite.TerminationEstablished ? "stopped" : RememberUncertain("vite", failures);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            return _vite.TerminationEstablished ? "stopped" : "uncertain";
        }
    }

    private async Task<string> StopApiAsync(List<Exception> failures)
    {
        try
        {
            await _api.DisposeAsync();
            return _api.TerminationEstablished ? "stopped" : RememberUncertain("api", failures);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            return _api.TerminationEstablished ? "stopped" : "uncertain";
        }
    }

    private static string RememberUncertain(string component, List<Exception> failures)
    {
        failures.Add(new InvalidOperationException("E2E could not confirm the " + component + " process stopped."));
        return "uncertain";
    }

    private async Task<string> DropOwnedDatabaseAsync(E2ECleanupDecision decision, List<Exception> failures)
    {
        if (!decision.DropOwnedDatabase)
        {
            return "left untouched " + _database.DatabaseName;
        }

        try
        {
            await _database.DisposeAsync();
            return "dropped " + _database.DatabaseName;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            return "failed to drop " + _database.DatabaseName;
        }
    }

    private string DeleteFixtures(List<Exception> failures)
    {
        try
        {
            if (Directory.Exists(FixtureDirectory))
            {
                Directory.Delete(FixtureDirectory, recursive: true);
            }

            return "deleted";
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            return "failed";
        }
    }

    private static async Task<string> CleanupStartupAsync(
        IBrowser? browser,
        IPlaywright? playwright,
        E2EViteProcess? vite,
        ProcessLifetime viteLifetime,
        E2EApiProcess? api,
        ProcessLifetime apiLifetime,
        E2EDatabase? database,
        string? fixtureDirectory)
    {
        List<string> outcomes = [];
        List<Exception> failures = [];
        if (browser is null && playwright is null)
        {
            outcomes.Add("browser not started");
        }
        else
        {
            try
            {
                if (browser is not null)
                {
                    await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }

                playwright?.Dispose();
                outcomes.Add("browser closed");
            }
            catch (Exception exception)
            {
                outcomes.Add("browser failed");
                failures.Add(exception);
            }
        }

        await StopStartupProcessAsync(vite, viteLifetime, "vite", outcomes, failures);
        await StopStartupProcessAsync(api, apiLifetime, "api", outcomes, failures);
        E2ECleanupDecision decision = StartupOwnership.Decide(
            api is not null,
            api is not null && api.TerminationEstablished,
            apiLifetime,
            vite is not null,
            vite is not null && vite.TerminationEstablished,
            viteLifetime,
            browserWorkOutstanding: false,
            browserStopFailed: outcomes.Contains("browser failed"));

        if (database is null)
        {
            outcomes.Add("database not created");
        }
        else if (!decision.DropOwnedDatabase)
        {
            outcomes.Add("database left untouched " + database.DatabaseName);
        }
        else
        {
            try
            {
                await database.DisposeAsync();
                outcomes.Add("database dropped " + database.DatabaseName);
            }
            catch (Exception exception)
            {
                outcomes.Add("database failed to drop " + database.DatabaseName);
                failures.Add(exception);
            }
        }

        try
        {
            if (fixtureDirectory is not null && Directory.Exists(fixtureDirectory))
            {
                Directory.Delete(fixtureDirectory, recursive: true);
            }

            outcomes.Add("fixtures deleted");
        }
        catch (Exception exception)
        {
            outcomes.Add("fixtures failed");
            failures.Add(exception);
        }

        if (failures.Count == 0 && !decision.FailForProcessLifecycle && !outcomes.Contains("fixtures failed"))
        {
            return string.Empty;
        }

        string detail = failures.Count == 0
            ? string.Empty
            : " " + string.Join(" | ", failures.ConvertAll(failure => failure.Message));
        return string.Join("; ", outcomes) + detail;
    }

    private static async Task StopStartupProcessAsync(
        IAsyncDisposable? process,
        ProcessLifetime lifetimeWhenAbsent,
        string name,
        List<string> outcomes,
        List<Exception> failures)
    {
        if (process is null)
        {
            if (!StartupOwnership.AllowsCleanup(false, false, lifetimeWhenAbsent))
            {
                outcomes.Add(name + " uncertain");
                failures.Add(new InvalidOperationException(
                    "E2E could not confirm the " + name + " process stopped."));
                return;
            }

            outcomes.Add(lifetimeWhenAbsent == ProcessLifetime.ConfirmedStopped
                ? name + " stopped"
                : name + " not started");
            return;
        }

        bool disposeFailed = false;
        try
        {
            await process.DisposeAsync();
        }
        catch (Exception exception)
        {
            disposeFailed = true;
            failures.Add(exception);
        }

        bool established = process switch
        {
            E2EApiProcess api => api.TerminationEstablished,
            E2EViteProcess vite => vite.TerminationEstablished,
            _ => false,
        };
        outcomes.Add(established ? name + " stopped" : name + " uncertain");
        if (!established && !disposeFailed)
        {
            failures.Add(new InvalidOperationException("E2E could not confirm the " + name + " process stopped."));
        }
    }

    private static async Task<T> WithinAsync<T>(Task<T> task, string stage, TimeSpan limit)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(limit));
        if (!ReferenceEquals(finished, task))
        {
            throw new TimeoutException(
                $"E2E stage '{stage}' exceeded {limit.TotalSeconds:0} seconds.");
        }

        return await task;
    }

    private void Watch(IPage page)
    {
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                _consoleErrors.Add(SensitiveText.Redact(message.Text, _signingKey, _accessToken));
            }
        };
        page.PageError += (_, error) =>
        {
            _pageErrors.Add(SensitiveText.Redact(error, _signingKey, _accessToken));
        };
        page.Response += (_, response) =>
        {
            if ((int)response.Status >= (int)HttpStatusCode.InternalServerError)
            {
                _serverErrors.Add($"{(int)response.Status} {response.Url}");
            }
        };
    }
}