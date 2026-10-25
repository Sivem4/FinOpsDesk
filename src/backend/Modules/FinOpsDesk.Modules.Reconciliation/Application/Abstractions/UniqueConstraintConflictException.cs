namespace FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

public sealed class UniqueConstraintConflictException : Exception
{
    public UniqueConstraintConflictException(string message)
        : base(message)
    {
    }

    public UniqueConstraintConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}