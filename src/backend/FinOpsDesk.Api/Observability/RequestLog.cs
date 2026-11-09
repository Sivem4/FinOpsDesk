using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

namespace FinOpsDesk.Api.Observability;

internal static partial class RequestLog
{
    public const string Category = "FinOpsDesk.Api.Requests";

    public const string Message = "HTTP request completed.";

    public const string HeaderName = "X-Request-Id";

    public const string RouteItemKey = "FinOpsDesk.RequestRoute";

    public const string ProblemTraceIdItemKey = "FinOpsDesk.ProblemTraceId";

    public const string ReadyRoute = "ready";

    public const string OidcCallbackRoute = "oidc-callback";

    public const string UnmatchedRoute = "unmatched";

    public const string OutcomeCompleted = "completed";

    public const string OutcomeCancelled = "cancelled";

    public const string OutcomeIncomplete = "incomplete";

    public static readonly EventId Completed = new(9101, "RequestCompleted");

    public static void Write(ILogger logger, RequestCompletedLog state)
    {
        ArgumentNullException.ThrowIfNull(logger);

        logger.Log(
            LogLevel.Information,
            Completed,
            state,
            exception: null,
            formatter: static (RequestCompletedLog _, Exception? _) => Message);
    }

    public static string? AcceptProblemTraceId(string? traceId, string requestId)
    {
        if (string.IsNullOrEmpty(traceId))
        {
            return null;
        }

        if (string.Equals(traceId, requestId, StringComparison.Ordinal))
        {
            return traceId;
        }

        return W3CTraceId().IsMatch(traceId) ? traceId : null;
    }

    public static string NormalizeMethod(string method)
    {
        if (HttpMethods.IsGet(method))
        {
            return "GET";
        }

        if (HttpMethods.IsPost(method))
        {
            return "POST";
        }

        if (HttpMethods.IsPut(method))
        {
            return "PUT";
        }

        if (HttpMethods.IsPatch(method))
        {
            return "PATCH";
        }

        if (HttpMethods.IsDelete(method))
        {
            return "DELETE";
        }

        if (HttpMethods.IsHead(method))
        {
            return "HEAD";
        }

        if (HttpMethods.IsOptions(method))
        {
            return "OPTIONS";
        }

        if (HttpMethods.IsTrace(method))
        {
            return "TRACE";
        }

        return "OTHER";
    }

    public static (string Outcome, int StatusCode) Classify(
        Exception? escaped,
        bool requestAborted,
        bool responseStarted,
        int responseStatusCode)
    {
        if (escaped is not null)
        {
            return (OutcomeIncomplete, responseStarted ? responseStatusCode : 0);
        }

        if (requestAborted)
        {
            if (responseStarted)
            {
                return (OutcomeCancelled, responseStatusCode);
            }

            return (
                OutcomeCancelled,
                responseStatusCode == StatusCodes.Status499ClientClosedRequest
                    ? StatusCodes.Status499ClientClosedRequest
                    : 0);
        }

        return (OutcomeCompleted, responseStatusCode);
    }

    [GeneratedRegex(@"\A[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex W3CTraceId();
}

internal readonly struct RequestCompletedLog : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly string _requestId;
    private readonly string? _problemTraceId;
    private readonly string _httpMethod;
    private readonly string _route;
    private readonly int _statusCode;
    private readonly long _elapsedMilliseconds;
    private readonly string _outcome;

    public RequestCompletedLog(
        string requestId,
        string? problemTraceId,
        string httpMethod,
        string route,
        int statusCode,
        long elapsedMilliseconds,
        string outcome)
    {
        _requestId = requestId;
        _problemTraceId = problemTraceId;
        _httpMethod = httpMethod;
        _route = route;
        _statusCode = statusCode;
        _elapsedMilliseconds = elapsedMilliseconds;
        _outcome = outcome;
    }

    public int Count => 7;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => new("requestId", _requestId),
        1 => new("problemTraceId", _problemTraceId),
        2 => new("httpMethod", _httpMethod),
        3 => new("route", _route),
        4 => new("statusCode", _statusCode),
        5 => new("elapsedMilliseconds", _elapsedMilliseconds),
        6 => new("outcome", _outcome),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    [ExcludeFromCodeCoverage]
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => RequestLog.Message;
}