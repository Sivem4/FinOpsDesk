using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

internal interface ICompetitorProbeQueries
{
    Task<CompetitorProbeQueryResult> ProbeInvoicesAsync(
        OrganizationId organizationId,
        Guid excludedInvoiceId,
        string currency,
        decimal remainingAmount,
        CancellationToken cancellationToken);

    Task<CompetitorProbeQueryResult> ProbeBankTransactionsAsync(
        OrganizationId organizationId,
        Guid excludedBankTransactionId,
        string currency,
        decimal remainingAmount,
        CancellationToken cancellationToken);
}