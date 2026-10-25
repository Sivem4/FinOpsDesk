using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Matching;

public sealed class GetExactMatchesHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IExactMatchCandidateQueries _candidateQueries;

    public GetExactMatchesHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IExactMatchCandidateQueries candidateQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(candidateQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _candidateQueries = candidateQueries;
    }

    public async Task<Result<ExactMatchResponse>> Handle(CancellationToken cancellationToken)
    {
        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;

        ExactMatchCandidateSet candidates = await _candidateQueries.GetOpenCandidatesAsync(
            organizationId,
            ExactMatchCandidateLimits.MaxPerSide,
            cancellationToken);

        if (candidates.InconsistentMoney)
        {
            return Result<ExactMatchResponse>.Failure(
                ReconciliationApplicationErrors.InconsistentMatchMoney);
        }

        if (candidates.InvoiceLimitExceeded || candidates.BankTransactionLimitExceeded)
        {
            return Result<ExactMatchResponse>.Failure(
                ReconciliationApplicationErrors.MatchCandidateLimitExceeded);
        }

        ExactMatchBatchResult evaluated = ExactMatchRules.Evaluate(
            organizationId,
            candidates.Invoices,
            candidates.BankTransactions);

        return Result<ExactMatchResponse>.Success(ExactMatchResponse.From(evaluated));
    }
}