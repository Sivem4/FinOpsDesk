namespace FinOpsDesk.Modules.Reconciliation.Application;

public enum FinancialCommitAuthorizationKind
{
    Unauthenticated = 1,
    Forbidden = 2,
    Unavailable = 3,
    LockUnavailable = 4,
}

// The financial transaction has been rolled back. The HTTP body uses Code and
// does not include a SQL error or a stored financial result.
public sealed class FinancialCommitAuthorizationException : Exception
{
    private FinancialCommitAuthorizationException(FinancialCommitAuthorizationKind kind, string code)
        : base("Financial commit authorization failed.")
    {
        Kind = kind;
        Code = code;
    }

    public FinancialCommitAuthorizationKind Kind { get; }

    public string Code { get; }

    public static FinancialCommitAuthorizationException Unauthenticated()
    {
        return new(FinancialCommitAuthorizationKind.Unauthenticated, "auth.commit.unauthenticated");
    }

    public static FinancialCommitAuthorizationException Forbidden()
    {
        return new(FinancialCommitAuthorizationKind.Forbidden, "auth.commit.forbidden");
    }

    public static FinancialCommitAuthorizationException Unavailable()
    {
        return new(FinancialCommitAuthorizationKind.Unavailable, "auth.authorization.unavailable");
    }

    public static FinancialCommitAuthorizationException LockUnavailable()
    {
        return new(FinancialCommitAuthorizationKind.LockUnavailable, "auth.authorization.lock_unavailable");
    }
}