using FinOpsDesk.Modules.Reconciliation.Presentation.Invoices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace FinOpsDesk.Modules.Reconciliation.Presentation;

public static class ReconciliationEndpoints
{
    public static RouteGroupBuilder MapReconciliationEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapInvoiceEndpoints();

        return group;
    }
}