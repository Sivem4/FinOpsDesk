using System.Diagnostics.CodeAnalysis;

namespace FinOpsDesk.SharedKernel.Results;

public sealed class Result
{
    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    private Result(Error? error)
    {
        Error = error;
    }

    public static Result Success()
    {
        return new Result(error: null);
    }

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new Result(error);
    }
}