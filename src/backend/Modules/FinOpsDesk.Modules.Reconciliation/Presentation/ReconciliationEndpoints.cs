using FinOpsDesk.Modules.Reconciliation.Presentation.Allocations;
using FinOpsDesk.Modules.Reconciliation.Presentation.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Presentation.Imports;
using FinOpsDesk.Modules.Reconciliation.Presentation.Invoices;
using FinOpsDesk.Modules.Reconciliation.Presentation.Matching;
using FinOpsDesk.Modules.Reconciliation.Presentation.ReviewCases;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace FinOpsDesk.Modules.Reconciliation.Presentation;

public static class ReconciliationEndpoints
{
    public static RouteGroupBuilder MapReconciliationEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.WithMetadata(new ReconciliationRouteBoundary());
        group.MapInvoiceEndpoints();
        group.MapBankTransactionEndpoints();
        group.MapImportEndpoints();
        group.MapMatchEndpoints();
        group.MapBatchMatchEndpoints();
        group.MapAllocationEndpoints();
        group.MapAllocationDisputeWorkEndpoints();
        group.MapAllocationDisputeEndpoints();
        group.MapAllocationDisputeResolutionEndpoints();
        group.MapAllocationDisputeCorrectionEndpoints();
        group.MapReviewCaseEndpoints();

        return group;
    }
}