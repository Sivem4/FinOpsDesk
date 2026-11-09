using Microsoft.Extensions.Logging;

namespace FinOpsDesk.IntegrationTests.Api;

internal sealed class RequestLogEntry
{
    public required int EventId { get; init; }

    public required string? EventName { get; init; }

    public required string Category { get; init; }

    public required string Message { get; init; }

    public required Exception? Exception { get; init; }

    public required IReadOnlyDictionary<string, object?> State { get; init; }
}

internal sealed class RequestLogCollector : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<RequestLogEntry> _entries = [];
    private int _target;
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<RequestLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _target = 0;
            _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public Task<IReadOnlyList<RequestLogEntry>> WaitForCountAsync(int count)
    {
        lock (_gate)
        {
            if (_entries.Count >= count)
            {
                return Task.FromResult<IReadOnlyList<RequestLogEntry>>(_entries.ToArray());
            }

            _target = count;
            _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return WaitAsync(_arrived.Task);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CategoryLogger(this, categoryName);
    }

    public void Dispose()
    {
    }

    private async Task<IReadOnlyList<RequestLogEntry>> WaitAsync(Task signal)
    {
        await signal.WaitAsync(TimeSpan.FromSeconds(8));
        return Entries;
    }

    private void Add(RequestLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_target > 0 && _entries.Count >= _target)
            {
                _arrived.TrySetResult();
            }
        }
    }

    private sealed class CategoryLogger(RequestLogCollector collector, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 9101)
            {
                return;
            }

            Dictionary<string, object?> values = [];
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (KeyValuePair<string, object?> pair in pairs)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            collector.Add(new RequestLogEntry
            {
                EventId = eventId.Id,
                EventName = eventId.Name,
                Category = category,
                Message = formatter(state, exception),
                Exception = exception,
                State = values,
            });
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}