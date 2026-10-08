namespace FinOpsDesk.Modules.Reconciliation.Application.Invoices;

public sealed record InvoiceListItemDto(
    Guid Id,
    string InvoiceNumber,
    MoneyDto TotalAmount,
    MoneyDto AllocatedAmount,
    MoneyDto OutstandingAmount);