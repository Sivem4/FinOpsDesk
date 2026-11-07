namespace FinOpsDesk.Modules.Access.Domain;

public readonly record struct AccessPermission
{
    private AccessPermission(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static AccessPermission InvoicesRead { get; } = new("invoices.read");

    public static AccessPermission BankTransactionsRead { get; } = new("bank-transactions.read");

    public static AccessPermission ReconciliationRead { get; } = new("reconciliation.read");

    public static AccessPermission ReconciliationConfirm { get; } = new("reconciliation.confirm");

    public static AccessPermission AllocationsRead { get; } = new("allocations.read");

    public static AccessPermission AllocationsCreate { get; } = new("allocations.create");

    public static AccessPermission AllocationsReverse { get; } = new("allocations.reverse");

    public static AccessPermission ReviewCasesRead { get; } = new("review-cases.read");

    public static AccessPermission ReviewCasesOpen { get; } = new("review-cases.open");

    public static AccessPermission ReviewCasesResolve { get; } = new("review-cases.resolve");

    public static AccessPermission ReviewCasesComment { get; } = new("review-cases.comment");

    public static AccessPermission ReviewCasesAssign { get; } = new("review-cases.assign");

    public static AccessPermission ReviewCasesAssignmentRead { get; } = new("review-cases.assignment.read");

    public static AccessPermission AllocationDisputesRegister { get; } = new("allocation-disputes.register");

    public static AccessPermission ImportsCreate { get; } = new("imports.create");

    public static AccessPermission AuditRead { get; } = new("audit.read");

    public static AccessPermission MembershipManage { get; } = new("membership.manage");

    public static AccessPermission RolesManage { get; } = new("roles.manage");

    public override string ToString()
    {
        return Value;
    }
}