namespace FinOpsDesk.Modules.Access.Application;

// Bounded wait for sp_getapplock. A session LOCK_TIMEOUT that is shorter than
// this value wins, so a caller that sets LOCK_TIMEOUT 0 fails immediately
// instead of waiting for the policy. -1 from the procedure is not success.
public sealed class SqlApplicationLockPolicy
{
    public const int DefaultWaitTimeoutMilliseconds = 10_000;

    public int WaitTimeoutMilliseconds { get; init; } = DefaultWaitTimeoutMilliseconds;
}