using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

internal sealed class E2ECommitUnprovenException : Exception
{
    public E2ECommitUnprovenException(string classification)
        : base(classification)
    {
    }
}

internal readonly record struct E2EJsonReply(
    int StatusCode,
    bool Replayed,
    Guid AllocationId,
    decimal Amount,
    string Currency,
    string? Status,
    string? Reason);

internal sealed class E2ECapturedRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public required string IdempotencyKey { get; init; }

    public required byte[] Body { get; init; }

    public bool SameIdentity(E2ECapturedRequest other)
    {
        return string.Equals(Method, other.Method, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(IdempotencyKey, other.IdempotencyKey, StringComparison.Ordinal)
            && Body.AsSpan().SequenceEqual(other.Body);
    }
}

internal sealed class E2ELossSession : IAsyncDisposable
{
    private readonly IPage _page;
    private readonly Regex _url;
    private readonly Func<IRoute, Task> _handler;
    private readonly TaskCompletionSource<E2ECapturedRequest> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<E2ECapturedRequest, IAPIResponse, Task> _prove;
    private readonly Func<Task<bool>> _sqlShowsCommit;
    private int _posts;
    private int _unexpected;
    private int _settled;
    private int _fetching;
    private int _routeDone;
    private int _disposed;

    private E2ELossSession(
        IPage page,
        Regex url,
        Func<IRoute, Task> handler,
        Func<E2ECapturedRequest, IAPIResponse, Task> prove,
        Func<Task<bool>> sqlShowsCommit)
    {
        _page = page;
        _url = url;
        _handler = handler;
        _prove = prove;
        _sqlShowsCommit = sqlShowsCommit;
    }

    public int PostCount => Volatile.Read(ref _posts);

    public int UnexpectedPosts => Volatile.Read(ref _unexpected);

    public static async Task<E2ELossSession> ArmAsync(
        IPage page,
        Regex url,
        Func<Task<bool>> sqlShowsCommit,
        Func<E2ECapturedRequest, IAPIResponse, Task> prove)
    {
        E2ELossSession? session = null;
        Func<IRoute, Task> handler = route => session!.HandleAsync(route);
        session = new E2ELossSession(page, url, handler, prove, sqlShowsCommit);
        await page.RouteAsync(url, handler);
        return session;
    }

    public async Task<E2ECapturedRequest> WaitAsync()
    {
        E2ECapturedRequest request = await _completed.Task.WaitAsync(TimeSpan.FromSeconds(40));
        if (Volatile.Read(ref _unexpected) != 0)
        {
            throw new E2ECommitUnprovenException(
                "A second financial POST was sent before the operator retried.");
        }

        return request;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _page.UnrouteAsync(_url, _handler);
    }

    private async Task HandleAsync(IRoute route)
    {
        try
        {
            if (!E2ELostResponse.IsPost(route))
            {
                await route.FallbackAsync();
                return;
            }

            int seen = Interlocked.Increment(ref _posts);
            if (Volatile.Read(ref _settled) == 1 || seen > 1)
            {
                Interlocked.Increment(ref _unexpected);
                await AbortAsync(route);
                _completed.TrySetException(new E2ECommitUnprovenException(
                    "A second financial POST was sent before the operator retried."));
                return;
            }

            await HandleFirstAsync(route);
        }
        catch (Exception exception)
        {
            await ReportAsync(route, exception);
        }
    }

    private async Task HandleFirstAsync(IRoute route)
    {
        E2ECapturedRequest captured = E2ELostResponse.Capture(route);
        if (Interlocked.Exchange(ref _fetching, 1) == 1)
        {
            throw new E2ECommitUnprovenException(
                "FetchAsync re-entered the route. The lost-response interception is not deterministic.");
        }

        IAPIResponse? response = null;
        try
        {
            try
            {
                response = await route.FetchAsync(new RouteFetchOptions
                {
                    MaxRetries = 0,
                    MaxRedirects = 0,
                    Timeout = 20_000,
                });
            }
            catch (Exception exception)
            {
                throw await ClassifyFetchFailureAsync(exception);
            }

            await _prove(captured, response);
            await AbortAsync(route);
            Volatile.Write(ref _settled, 1);
            _completed.TrySetResult(captured);
        }
        finally
        {
            Volatile.Write(ref _fetching, 0);
        }
    }

    private async Task<Exception> ClassifyFetchFailureAsync(Exception exception)
    {
        bool committed;
        try
        {
            committed = await _sqlShowsCommit();
        }
        catch (Exception sqlFailure)
        {
            return new E2ECommitUnprovenException(
                "SQL could not be read after FetchAsync failed. The commit was not proven. "
                + sqlFailure.GetType().Name);
        }

        string classification = committed
            ? "C rejected. SQL shows a commit, but FetchAsync did not return the server response. "
            : "A/B. FetchAsync did not return a response and SQL does not show a commit. ";
        return new E2ECommitUnprovenException(classification + exception.GetType().Name);
    }

    private async Task ReportAsync(IRoute route, Exception exception)
    {
        Exception toReport = exception;
        if (Volatile.Read(ref _routeDone) == 0)
        {
            try
            {
                await AbortAsync(route);
            }
            catch (Exception abortFailure)
            {
                toReport = new AggregateException(exception, abortFailure);
            }
        }

        _completed.TrySetException(toReport);
    }

    private async Task AbortAsync(IRoute route)
    {
        await route.AbortAsync("failed");
        Volatile.Write(ref _routeDone, 1);
    }
}

internal sealed class E2EPostWatch : IAsyncDisposable
{
    private readonly IPage _page;
    private readonly IReadOnlyList<(Regex Url, Func<IRoute, Task> Handler)> _routes;
    private int _count;
    private int _disposed;

    private E2EPostWatch(IPage page, IReadOnlyList<(Regex Url, Func<IRoute, Task> Handler)> routes)
    {
        _page = page;
        _routes = routes;
    }

    public int Count => Volatile.Read(ref _count);

    public static async Task<E2EPostWatch> StartAsync(IPage page, params Regex[] urls)
    {
        E2EPostWatch? watch = null;
        List<(Regex Url, Func<IRoute, Task> Handler)> routes = [];
        foreach (Regex url in urls)
        {
            Regex pattern = url;
            Func<IRoute, Task> handler = route => watch!.HandleAsync(route, pattern);
            routes.Add((url, handler));
        }

        watch = new E2EPostWatch(page, routes);
        foreach ((Regex url, Func<IRoute, Task> handler) in routes)
        {
            await page.RouteAsync(url, handler);
        }

        return watch;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach ((Regex url, Func<IRoute, Task> handler) in _routes)
        {
            await _page.UnrouteAsync(url, handler);
        }
    }

    private async Task HandleAsync(IRoute route, Regex url)
    {
        if (E2ELostResponse.IsPost(route) && url.IsMatch(route.Request.Url))
        {
            Interlocked.Increment(ref _count);
            await route.AbortAsync("failed");
            return;
        }

        await route.FallbackAsync();
    }
}

internal static class E2ELostResponse
{
    public static readonly Regex ReversalUrl = new(
        @"/api/allocations/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/reverse$",
        RegexOptions.None);

    public static readonly Regex ExplicitAmountUrl = new(
        @"/api/allocations$",
        RegexOptions.None);

    public const string UnknownReversal =
        "The reversal was not verified. It may already have been saved. The stored request was kept, and it was not sent again.";

    public const string UnknownAllocation =
        "The request ended before a result was received. Stopping the request does not show that the server rolled the allocation back. Send the same stored attempt again.";

    public static async Task<E2ECapturedRequest> ReplayAsync(
        IPage page,
        Regex url,
        E2ECapturedRequest original,
        Func<Task> send,
        Func<IAPIResponse, Task> verify)
    {
        TaskCompletionSource<E2ECapturedRequest> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int posts = 0;
        Func<IRoute, Task> handler = async route =>
        {
            try
            {
                if (!IsPost(route))
                {
                    await route.FallbackAsync();
                    return;
                }

                if (Interlocked.Increment(ref posts) > 1)
                {
                    await route.AbortAsync("failed");
                    completed.TrySetException(new E2ECommitUnprovenException(
                        "More than one replay POST was sent."));
                    return;
                }

                E2ECapturedRequest captured = Capture(route);
                if (!captured.SameIdentity(original))
                {
                    await route.AbortAsync("failed");
                    completed.TrySetException(new E2ECommitUnprovenException(
                        "The replay POST did not repeat the original method, path, idempotency key, and body."));
                    return;
                }

                IAPIResponse response = await route.FetchAsync(new RouteFetchOptions
                {
                    MaxRetries = 0,
                    MaxRedirects = 0,
                    Timeout = 20_000,
                });
                await verify(response);
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
                completed.TrySetResult(captured);
            }
            catch (Exception exception)
            {
                try
                {
                    await route.AbortAsync("failed");
                }
                catch (Exception abortFailure)
                {
                    completed.TrySetException(new AggregateException(exception, abortFailure));
                    return;
                }

                completed.TrySetException(exception);
            }
        };

        await page.RouteAsync(url, handler);
        try
        {
            await send();
            return await completed.Task.WaitAsync(TimeSpan.FromSeconds(40));
        }
        finally
        {
            await page.UnrouteAsync(url, handler);
        }
    }

    public static async Task ProveReversalCommitAsync(
        E2EHost host,
        E2ECapturedRequest request,
        IAPIResponse response,
        Guid allocationId,
        string reason)
    {
        E2EBrowser.RequireNoOrganizationId(request, host.OrganizationId);
        (bool ok, E2EJsonReply reply) = await ReadReplyAsync(response);
        bool committed = await E2EFinance.IsAllocationReversedAsync(
            host.ConnectionString,
            host.OrganizationId,
            allocationId);
        bool identity = ok
            && response.Status == 200
            && !reply.Replayed
            && reply.AllocationId == allocationId
            && reply.Amount == E2EWorkspace.OpenAmount
            && reply.Currency == "PLN"
            && string.Equals(reply.Status, "reversed", StringComparison.Ordinal)
            && string.Equals(reply.Reason, reason, StringComparison.Ordinal);
        if (!identity || !committed)
        {
            throw new E2ECommitUnprovenException(Classify(committed, identity, response.Status, "reversal"));
        }

        await E2EFinance.RequireReversedAsync(
            host.ConnectionString,
            host.OrganizationId,
            allocationId,
            E2EWorkspace.OpenAmount,
            reason);
    }

    public static async Task<Guid> ProveExplicitCommitAsync(
        E2EHost host,
        E2ECapturedRequest request,
        IAPIResponse response,
        Guid invoiceId,
        Guid bankTransactionId)
    {
        E2EBrowser.RequireNoOrganizationId(request, host.OrganizationId);
        (bool ok, E2EJsonReply reply) = await ReadReplyAsync(response);
        bool committed = await E2EFinance.HasPartialAllocationAsync(
            host.ConnectionString,
            host.OrganizationId,
            invoiceId,
            E2EWorkspace.PartialAmount);
        bool identity = ok
            && response.Status == 201
            && !reply.Replayed
            && reply.AllocationId != Guid.Empty
            && reply.Amount == E2EWorkspace.PartialAmount
            && reply.Currency == "PLN";
        if (!identity || !committed)
        {
            throw new E2ECommitUnprovenException(Classify(committed, identity, response.Status, "allocation"));
        }

        await E2EFinance.RequirePartialAsync(
            host.ConnectionString,
            host.OrganizationId,
            reply.AllocationId,
            invoiceId,
            bankTransactionId);
        return reply.AllocationId;
    }

    public static async Task RequireReplayAsync(
        IAPIResponse response,
        Guid allocationId,
        decimal amount,
        string? status,
        string? reason)
    {
        (bool ok, E2EJsonReply reply) = await ReadReplyAsync(response);
        if (!ok
            || response.Status != 200
            || !reply.Replayed
            || reply.AllocationId != allocationId
            || reply.Amount != amount
            || reply.Currency != "PLN"
            || (status is not null && !string.Equals(reply.Status, status, StringComparison.Ordinal))
            || (reason is not null && !string.Equals(reply.Reason, reason, StringComparison.Ordinal)))
        {
            throw new E2ECommitUnprovenException(
                "The replay was not HTTP 200 with replayed true for the original financial request.");
        }
    }

    public static async Task<(bool Ok, E2EJsonReply Reply)> ReadReplyAsync(IAPIResponse response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
            JsonElement root = document.RootElement;
            JsonElement amount = root.GetProperty("amount");
            E2EJsonReply reply = new(
                response.Status,
                root.GetProperty("replayed").GetBoolean(),
                root.GetProperty("allocationId").GetGuid(),
                ReadDecimal(amount.GetProperty("amount")),
                amount.GetProperty("currency").GetString() ?? string.Empty,
                root.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String
                    ? status.GetString()
                    : null,
                root.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null);
            return (true, reply);
        }
        catch (Exception)
        {
            return (false, default);
        }
    }

    internal static bool IsPost(IRoute route)
    {
        return string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase);
    }

    internal static E2ECapturedRequest Capture(IRoute route)
    {
        if (!Uri.TryCreate(route.Request.Url, UriKind.Absolute, out Uri? uri))
        {
            throw new E2ECommitUnprovenException("The financial POST URL could not be read.");
        }

        byte[]? body = route.Request.PostDataBuffer;
        if (body is null || body.Length == 0)
        {
            throw new E2ECommitUnprovenException("The financial POST did not include a body.");
        }

        return new E2ECapturedRequest
        {
            Method = route.Request.Method,
            Path = uri.AbsolutePath,
            IdempotencyKey = RequireIdempotencyKey(route.Request.Headers),
            Body = body.ToArray(),
        };
    }

    private static string Classify(bool committed, bool identity, int statusCode, string operation)
    {
        if (committed && !identity)
        {
            return "C rejected. SQL shows a commit, but the HTTP response was not a first successful " + operation + ".";
        }

        if (!committed && identity)
        {
            return "B. The server returned a first-success " + operation + " response, but SQL does not show the commit.";
        }

        return "B. The " + operation + " response did not prove a first commit and SQL does not show one. HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + ".";
    }

    private static string RequireIdempotencyKey(IDictionary<string, string> headers)
    {
        foreach ((string key, string value) in headers)
        {
            if (key.Equals("idempotency-key", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new E2ECommitUnprovenException("The financial POST did not include an Idempotency-Key.");
    }

    private static decimal ReadDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.GetDecimal();
        }

        if (element.ValueKind == JsonValueKind.String
            && decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
        {
            return parsed;
        }

        throw new FormatException("The response amount was not a decimal.");
    }
}