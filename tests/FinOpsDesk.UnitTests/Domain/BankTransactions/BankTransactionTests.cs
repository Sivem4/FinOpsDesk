using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.BankTransactions;

public sealed class BankTransactionTests
{
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");

    [Fact]
    public void New_transaction_should_be_fully_unapplied()
    {
        var transaction = CreateTransaction(1000m);

        Assert.Equal(
            Money.Zero(Pln),
            transaction.AllocatedAmount);

        Assert.Equal(
            new Money(1000m, Pln),
            transaction.UnappliedAmount);
    }

    [Fact]
    public void Allocate_should_reduce_unapplied_amount()
    {
        var transaction = CreateTransaction(1000m);

        transaction.Allocate(
            new Money(300m, Pln));

        Assert.Equal(
            new Money(300m, Pln),
            transaction.AllocatedAmount);

        Assert.Equal(
            new Money(700m, Pln),
            transaction.UnappliedAmount);
    }

    [Fact]
    public void Multiple_allocations_should_accumulate()
    {
        var transaction = CreateTransaction(1000m);

        transaction.Allocate(
            new Money(300m, Pln));

        transaction.Allocate(
            new Money(200m, Pln));

        Assert.Equal(
            new Money(500m, Pln),
            transaction.AllocatedAmount);

        Assert.Equal(
            new Money(500m, Pln),
            transaction.UnappliedAmount);
    }

    [Fact]
    public void Allocate_should_allow_entire_unapplied_amount()
    {
        var transaction = CreateTransaction(1000m);

        transaction.Allocate(
            new Money(1000m, Pln));

        Assert.Equal(
            Money.Zero(Pln),
            transaction.UnappliedAmount);
    }

    [Fact]
    public void Allocate_should_reject_amount_greater_than_unapplied()
    {
        var transaction = CreateTransaction(1000m);

        Assert.Throws<InvalidOperationException>(
            () => transaction.Allocate(
                new Money(1001m, Pln)));

        Assert.Equal(
            new Money(1000m, Pln),
            transaction.UnappliedAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Allocate_should_reject_non_positive_amount(
        decimal amount)
    {
        var transaction = CreateTransaction(1000m);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => transaction.Allocate(
                new Money(amount, Pln)));
    }

    [Fact]
    public void Allocate_should_reject_different_currency()
    {
        var transaction = CreateTransaction(1000m);

        Assert.Throws<InvalidOperationException>(
            () => transaction.Allocate(
                new Money(100m, Eur)));
    }

    [Fact]
    public void ReverseAllocation_should_restore_unapplied_amount()
    {
        var transaction = CreateTransaction(1000m);

        transaction.Allocate(
            new Money(600m, Pln));

        transaction.ReverseAllocation(
            new Money(200m, Pln));

        Assert.Equal(
            new Money(400m, Pln),
            transaction.AllocatedAmount);

        Assert.Equal(
            new Money(600m, Pln),
            transaction.UnappliedAmount);
    }

    [Fact]
    public void ReverseAllocation_should_reject_amount_greater_than_allocated()
    {
        var transaction = CreateTransaction(1000m);

        transaction.Allocate(
            new Money(300m, Pln));

        Assert.Throws<InvalidOperationException>(
            () => transaction.ReverseAllocation(
                new Money(301m, Pln)));

        Assert.Equal(
            new Money(300m, Pln),
            transaction.AllocatedAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Constructor_should_reject_non_positive_amount(
        decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BankTransaction(
                BankTransactionId.New(),
                new Money(amount, Pln),
                new DateOnly(2026, 8, 24),
                "INV-001"));
    }

    [Fact]
    public void Constructor_should_trim_reference()
    {
        var transaction = new BankTransaction(
            BankTransactionId.New(),
            new Money(1000m, Pln),
            new DateOnly(2026, 8, 24),
            "  INV-001  ");

        Assert.Equal(
            "INV-001",
            transaction.Reference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_reference_should_be_normalized_to_null(
        string? reference)
    {
        var transaction = new BankTransaction(
            BankTransactionId.New(),
            new Money(1000m, Pln),
            new DateOnly(2026, 8, 24),
            reference);

        Assert.Null(transaction.Reference);
    }

    private static BankTransaction CreateTransaction(decimal amount)
    {
        return new BankTransaction(
            BankTransactionId.New(),
            new Money(amount, Pln),
            new DateOnly(2026, 8, 24),
            "INV-001");
    }
}