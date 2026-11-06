namespace FinOpsDesk.Modules.Reconciliation.Presentation;

// Stable write identities. The host allowlist matches these values.
// A permission name is not an identity: confirmation and batch execution share
// reconciliation.confirm and remain different operations.
public enum FinancialWriteOperation
{
    ImportInvoices = 1,
    ImportBankTransactions = 2,
    ConfirmExactMatch = 3,
    ExecuteBatchExactMatches = 4,
    CreateAllocation = 5,
    ReverseAllocation = 6,
    OpenReviewCase = 7,
    ResolveReviewCase = 8,
    DismissReviewCase = 9,
    UnlistedProbe = 10,
    CommentReviewCase = 11,
}

// Endpoint metadata read by the host authorization policy. It is not itself
// an authorization requirement and it does not grant access.
public sealed class FinancialEndpointClassification
{
    private FinancialEndpointClassification(
        OperationKind kind,
        string? permission,
        FinancialWriteOperation? operation)
    {
        Kind = kind;
        Permission = permission;
        Operation = operation;
    }

    public OperationKind Kind { get; }

    public string? Permission { get; }

    public FinancialWriteOperation? Operation { get; }

    public static FinancialEndpointClassification Read(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        return new FinancialEndpointClassification(OperationKind.Read, permission, null);
    }

    public static FinancialEndpointClassification Write(string permission, FinancialWriteOperation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Financial write operation is not defined.");
        }

        return new FinancialEndpointClassification(OperationKind.Write, permission, operation);
    }

    // A write without a known permission is intentionally inconsistent.
    // Classification probes use it to prove that the host fails closed.
    public static FinancialEndpointClassification Write()
    {
        return new FinancialEndpointClassification(OperationKind.Write, null, null);
    }

    public enum OperationKind
    {
        Read = 1,
        Write = 2,
    }
}