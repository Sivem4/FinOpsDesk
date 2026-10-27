using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public readonly record struct BatchExactMatchPair(
    InvoiceId InvoiceId,
    BankTransactionId BankTransactionId);