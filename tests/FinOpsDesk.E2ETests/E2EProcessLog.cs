using System.Collections.Concurrent;
using System.Diagnostics;

namespace FinOpsDesk.E2ETests;

internal static class ProcessExit
{
    public static string Describe(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString() : "running";
        }
        catch (InvalidOperationException)
        {
            return "unavailable";
        }
    }
}

internal sealed class E2EProcessLog
{
    private const int MaxLines = 40;

    private readonly ConcurrentQueue<string> _lines = new();
    private readonly string[] _secrets;

    public E2EProcessLog(params string?[] secrets)
    {
        _secrets = secrets.Where(secret => !string.IsNullOrEmpty(secret)).Cast<string>().ToArray();
    }

    public void Attach(Process process)
    {
        process.OutputDataReceived += (_, eventArgs) => Add(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => Add(eventArgs.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public string Tail()
    {
        return SensitiveText.Redact(string.Join(Environment.NewLine, _lines), _secrets);
    }

    private void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _lines.Enqueue(SensitiveText.Redact(line, _secrets));
        while (_lines.Count > MaxLines && _lines.TryDequeue(out _))
        {
        }
    }
}