using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public readonly record struct ReviewBlockSubject(
    ReviewCaseStatus Status,
    ReviewCaseReason Reason,
    InvoiceId? InvoiceId,
    BankTransactionId? BankTransactionId);