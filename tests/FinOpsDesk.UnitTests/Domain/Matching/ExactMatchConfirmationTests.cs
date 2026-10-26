using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Matching;

public sealed class ExactMatchConfirmationTests
{
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");

    [Fact]
    public void TryDeriveAmount_should_return_the_shared_open_amount()
    {
        Result<Money> result = ExactMatchConfirmation.TryDeriveAmount(
            " inv-001 ",
            "INV-001",
            new Money(125.50m, Pln),
            new Money(125.50m, Pln));

        Assert.True(result.IsSuccess);
        Assert.Equal(new Money(125.50m, Pln), result.Value);
    }

    [Theory]
    [InlineData("INV-001", "INV-002")]
    [InlineData("INV 001", "INV001")]
    [InlineData("INV-001", null)]
    [InlineData("INV-001", " ")]
    public void TryDeriveAmount_should_reject_a_reference_mismatch(
        string invoiceNumber,
        string? bankReference)
    {
        Result<Money> result = ExactMatchConfirmation.TryDeriveAmount(
            invoiceNumber,
            bankReference,
            new Money(10m, Pln),
            new Money(10m, Pln));

        Assert.Equal(ReconciliationErrors.MatchReferenceMismatch.Code, result.Error?.Code);
    }

    [Fact]
    public void TryDeriveAmount_should_reject_different_currencies()
    {
        Result<Money> result = ExactMatchConfirmation.TryDeriveAmount(
            "INV-001",
            "INV-001",
            new Money(10m, Pln),
            new Money(10m, Eur));

        Assert.Equal(ReconciliationErrors.MatchCurrencyMismatch.Code, result.Error?.Code);
    }

    [Fact]
    public void TryDeriveAmount_should_reject_unequal_open_amounts()
    {
        Result<Money> result = ExactMatchConfirmation.TryDeriveAmount(
            "INV-001",
            "INV-001",
            new Money(10m, Pln),
            new Money(4m, Pln));

        Assert.Equal(ReconciliationErrors.MatchStale.Code, result.Error?.Code);
    }

    [Fact]
    public void TryDeriveAmount_should_reject_a_non_positive_open_amount()
    {
        Result<Money> result = ExactMatchConfirmation.TryDeriveAmount(
            "INV-001",
            "INV-001",
            new Money(0m, Pln),
            new Money(0m, Pln));

        Assert.Equal(ReconciliationErrors.MatchStale.Code, result.Error?.Code);
    }
}