namespace FinOpsDesk.Modules.Access.Application;

public enum OrganizationAuthorizationFailure
{
    Unauthenticated = 1,
    Forbidden = 2,
    Unavailable = 3,
    LockNotAcquired = 4,
}

// Raised after the financial or administrative transaction has been rolled
// back. LockReturnCode is the sp_getapplock code when the failure is a lock,
// and zero otherwise. It is a diagnostic, not an HTTP body.
public sealed class OrganizationAuthorizationException : Exception
{
    public OrganizationAuthorizationException(OrganizationAuthorizationFailure failure, int lockReturnCode)
        : base("Organization authorization did not succeed.")
    {
        Failure = failure;
        LockReturnCode = lockReturnCode;
    }

    public OrganizationAuthorizationFailure Failure { get; }

    public int LockReturnCode { get; }
}