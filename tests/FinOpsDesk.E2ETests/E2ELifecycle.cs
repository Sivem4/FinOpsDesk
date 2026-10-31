using System.ComponentModel;
using System.Diagnostics;

namespace FinOpsDesk.E2ETests;

internal readonly record struct ProcessStopResult(bool Stopped, string Detail);

internal readonly record struct E2ECleanupDecision(bool DropOwnedDatabase, bool FailForProcessLifecycle);

internal static class ProcessTermination
{
    public static async Task<ProcessStopResult> ConfirmStoppedAsync(Process process, TimeSpan waitLimit)
    {
        try
        {
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(waitLimit);
            }

            bool exited = HasExited(process);
            return new ProcessStopResult(exited, exited ? "exited" : "still running");
        }
        catch (TimeoutException)
        {
            bool exited = TryHasExited(process);
            return new ProcessStopResult(exited, exited ? "exited" : "still running");
        }
        catch (InvalidOperationException)
        {
            bool exited = TryHasExited(process);
            return new ProcessStopResult(exited, exited ? "exited" : "unavailable");
        }
        catch (Win32Exception exception)
        {
            return new ProcessStopResult(false, SensitiveText.Redact(exception.Message));
        }
    }

    public static bool IsAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Refresh();
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasExited(Process process)
    {
        process.Refresh();
        return process.HasExited;
    }

    private static bool TryHasExited(Process process)
    {
        try
        {
            return HasExited(process);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal static class E2ECleanupPolicy
{
    public static E2ECleanupDecision Decide(
        bool browserWorkOutstanding,
        bool apiTerminationEstablished,
        bool viteTerminationEstablished,
        bool browserStopFailed)
    {
        if (browserWorkOutstanding || !apiTerminationEstablished)
        {
            return new E2ECleanupDecision(DropOwnedDatabase: false, FailForProcessLifecycle: true);
        }

        bool fail = !viteTerminationEstablished || browserStopFailed;
        return new E2ECleanupDecision(DropOwnedDatabase: true, FailForProcessLifecycle: fail);
    }
}

internal static class E2EJourneyLimit
{
    public static async Task WaitAsync(
        Task journey,
        TimeSpan limit,
        Func<Task> stopBrowser,
        TimeSpan stopGrace)
    {
        using CancellationTokenSource timeout = new();
        timeout.CancelAfter(limit);
        Task finished = await Task.WhenAny(journey, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
        if (ReferenceEquals(finished, journey))
        {
            await journey;
            return;
        }

        Exception? stopFailure = null;
        try
        {
            await stopBrowser();
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        try
        {
            await journey.WaitAsync(stopGrace);
        }
        catch (TimeoutException exception)
        {
            throw new E2EJourneyOutstandingException(
                "The browser journey exceeded " + Format(limit)
                + " and was still running after the browser stop request. "
                + "The owned database will not be dropped while that work is outstanding.",
                stopFailure ?? exception);
        }

        if (journey.IsFaulted)
        {
            Exception journeyFailure = journey.Exception!.GetBaseException();
            throw new TimeoutException(
                "The browser journey exceeded " + Format(limit)
                + ". Browser work stopped before cleanup. "
                + journeyFailure.Message,
                journeyFailure);
        }

        if (stopFailure is not null)
        {
            throw new TimeoutException(
                "The browser journey exceeded " + Format(limit)
                + ". The journey task finished after the browser stop request. "
                + stopFailure.Message,
                stopFailure);
        }

        throw new TimeoutException(
            "The browser journey exceeded " + Format(limit) + ". Browser work stopped before cleanup.");
    }

    private static string Format(TimeSpan limit)
    {
        return limit.TotalSeconds.ToString("0") + " seconds";
    }
}

internal sealed class E2EJourneyOutstandingException : TimeoutException
{
    public E2EJourneyOutstandingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

internal enum ProcessLifetime
{
    NotStarted,
    ConfirmedStopped,
    Unconfirmed,
}

internal sealed class ProcessStartupException : Exception
{
    public ProcessStartupException(ProcessLifetime lifetime, Exception innerException)
        : base("Process startup failed.", innerException)
    {
        Lifetime = lifetime;
    }

    public ProcessLifetime Lifetime { get; }
}

internal static class StartupCompletion
{
    public static async Task CompleteAsync(
        Func<Task> waitUntilReady,
        Func<ValueTask> dispose,
        Func<bool> terminationEstablished)
    {
        try
        {
            await waitUntilReady();
        }
        catch (Exception readinessFailure)
        {
            Exception? disposeFailure = null;
            try
            {
                await dispose();
            }
            catch (Exception exception)
            {
                disposeFailure = exception;
            }

            ProcessLifetime lifetime = terminationEstablished()
                ? ProcessLifetime.ConfirmedStopped
                : ProcessLifetime.Unconfirmed;
            Exception reported = disposeFailure is null
                ? readinessFailure
                : new AggregateException(readinessFailure, disposeFailure);
            throw new ProcessStartupException(lifetime, reported);
        }
    }

    public static async Task<ProcessStartupException> AbandonAsync(Process process, Exception exception)
    {
        try
        {
            ProcessStopResult result = await ProcessTermination.ConfirmStoppedAsync(process, TimeSpan.FromSeconds(15));
            process.Dispose();
            return new ProcessStartupException(
                result.Stopped ? ProcessLifetime.ConfirmedStopped : ProcessLifetime.Unconfirmed,
                exception);
        }
        catch (Exception stopFailure)
        {
            try
            {
                process.Dispose();
            }
            catch (Exception disposeFailure)
            {
                return new ProcessStartupException(
                    ProcessLifetime.Unconfirmed,
                    new AggregateException(exception, stopFailure, disposeFailure));
            }

            return new ProcessStartupException(
                ProcessLifetime.Unconfirmed,
                new AggregateException(exception, stopFailure));
        }
    }
}

internal static class StartupOwnership
{
    public static bool AllowsCleanup(bool objectHeld, bool terminationEstablished, ProcessLifetime lifetimeWhenAbsent)
    {
        if (objectHeld)
        {
            return terminationEstablished;
        }

        return lifetimeWhenAbsent != ProcessLifetime.Unconfirmed;
    }

    public static E2ECleanupDecision Decide(
        bool apiObjectHeld,
        bool apiTerminationEstablished,
        ProcessLifetime apiLifetimeWhenAbsent,
        bool viteObjectHeld,
        bool viteTerminationEstablished,
        ProcessLifetime viteLifetimeWhenAbsent,
        bool browserWorkOutstanding,
        bool browserStopFailed)
    {
        return E2ECleanupPolicy.Decide(
            browserWorkOutstanding,
            AllowsCleanup(apiObjectHeld, apiTerminationEstablished, apiLifetimeWhenAbsent),
            AllowsCleanup(viteObjectHeld, viteTerminationEstablished, viteLifetimeWhenAbsent),
            browserStopFailed);
    }
}