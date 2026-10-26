using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record ConfirmedAllocationRecord(
    AllocationId AllocationId,
    InvoiceId InvoiceId,
    BankTransactionId BankTransactionId,
    Money Amount);