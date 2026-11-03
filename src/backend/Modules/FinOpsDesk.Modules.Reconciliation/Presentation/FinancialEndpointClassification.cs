namespace FinOpsDesk.Modules.Reconciliation.Presentation;

// Endpoint metadata read by the host authorization policy. It is not itself
// an authorization requirement and it does not grant access.
public sealed class FinancialEndpointClassification
{
    private FinancialEndpointClassification(OperationKind kind, string? permission)
    {
        Kind = kind;
        Permission = permission;
    }

    public OperationKind Kind { get; }

    public string? Permission { get; }

    public static FinancialEndpointClassification Read(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        return new FinancialEndpointClassification(OperationKind.Read, permission);
    }

    public static FinancialEndpointClassification Write(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        return new FinancialEndpointClassification(OperationKind.Write, permission);
    }

    // A write without a known permission is intentionally inconsistent.
    // Classification probes use it to prove that the host fails closed.
    public static FinancialEndpointClassification Write()
    {
        return new FinancialEndpointClassification(OperationKind.Write, null);
    }

    public enum OperationKind
    {
        Read = 1,
        Write = 2,
    }
}