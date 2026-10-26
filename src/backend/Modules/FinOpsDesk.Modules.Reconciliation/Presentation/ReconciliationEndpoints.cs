using FinOpsDesk.Modules.Reconciliation.Presentation.Allocations;
using FinOpsDesk.Modules.Reconciliation.Presentation.Imports;
using FinOpsDesk.Modules.Reconciliation.Presentation.Invoices;
using FinOpsDesk.Modules.Reconciliation.Presentation.Matching;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace FinOpsDesk.Modules.Reconciliation.Presentation;

public static class ReconciliationEndpoints
{
    public static RouteGroupBuilder MapReconciliationEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapInvoiceEndpoints();
        group.MapImportEndpoints();
        group.MapMatchEndpoints();
        group.MapAllocationEndpoints();

        return group;
    }
}