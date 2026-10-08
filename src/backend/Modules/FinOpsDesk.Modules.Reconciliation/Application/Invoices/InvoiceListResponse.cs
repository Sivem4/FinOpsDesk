namespace FinOpsDesk.Modules.Reconciliation.Application.Invoices;

public sealed record InvoiceListResponse(
    IReadOnlyList<InvoiceListItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount);