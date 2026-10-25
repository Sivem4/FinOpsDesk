namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public enum MatchReasonCode
{
    ExactReferenceCurrencyAndOpenAmount = 1,
    CompetingInvoices = 2,
    CompetingTransactions = 3,
    CompetingInvoicesAndTransactions = 4,
    NoCounterpart = 5,
    MissingReference = 6,
    NonPositiveOpenAmount = 7
}