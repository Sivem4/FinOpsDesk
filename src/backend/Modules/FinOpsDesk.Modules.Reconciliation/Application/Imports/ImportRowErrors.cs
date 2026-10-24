using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public static class ImportRowErrors
{
    public static Error InvalidExternalId { get; } =
        new(
            "reconciliation.import.invalid_external_id",
            "External id is missing or longer than 128 characters.");

    public static Error InvalidInvoiceNumber { get; } =
        new(
            "reconciliation.import.invalid_invoice_number",
            "Invoice number is required.");

    public static Error InvalidAmount { get; } =
        new(
            "reconciliation.import.invalid_amount",
            "Amount must be a positive decimal(19,4) value without rounding.");

    public static Error InvalidCurrency { get; } =
        new(
            "reconciliation.import.invalid_currency",
            "Currency must be a three-letter code.");

    public static Error InvalidBookingDate { get; } =
        new(
            "reconciliation.import.invalid_booking_date",
            "Booking date must use the yyyy-MM-dd format.");

    public static Error SourceConflict => ReconciliationErrors.ImportSourceConflict;
}