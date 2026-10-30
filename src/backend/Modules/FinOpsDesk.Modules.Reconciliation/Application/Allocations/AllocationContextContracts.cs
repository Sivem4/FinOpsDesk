using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record GetAllocationContextQuery(
    InvoiceId InvoiceId,
    BankTransactionId BankTransactionId);

public sealed record AllocationContextResponse(
    AllocationContextInvoice Invoice,
    AllocationContextBankTransaction BankTransaction);

public sealed record AllocationContextInvoice(
    Guid InvoiceId,
    string InvoiceNumber,
    MoneyDto TotalAmount,
    MoneyDto AllocatedAmount,
    MoneyDto OutstandingAmount,
    string? SourceSystem,
    string? ExternalId);

public sealed record AllocationContextBankTransaction(
    Guid BankTransactionId,
    string? Reference,
    DateOnly BookingDate,
    MoneyDto Amount,
    MoneyDto AllocatedAmount,
    MoneyDto UnappliedAmount,
    string? SourceSystem,
    string? ExternalId);

public sealed record AllocationContextInvoiceColumns(
    Guid InvoiceId,
    string InvoiceNumber,
    decimal TotalAmount,
    string TotalCurrency,
    decimal AllocatedAmount,
    string AllocatedCurrency,
    string? SourceSystem,
    string? ExternalId);

public sealed record AllocationContextBankTransactionColumns(
    Guid BankTransactionId,
    string? Reference,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    decimal AllocatedAmount,
    string AllocatedCurrency,
    string? SourceSystem,
    string? ExternalId);