using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed record MatchBankTransactionSnapshot
{
    public OrganizationId OrganizationId { get; }

    public BankTransactionId BankTransactionId { get; }

    public string? Reference { get; }

    public Money UnappliedAmount { get; }

    public MatchBankTransactionSnapshot(
        OrganizationId organizationId,
        BankTransactionId bankTransactionId,
        string? reference,
        Money unappliedAmount)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);
        ArgumentNullException.ThrowIfNull(unappliedAmount);

        OrganizationId = organizationId;
        BankTransactionId = bankTransactionId;
        Reference = reference;
        UnappliedAmount = unappliedAmount;
    }
}