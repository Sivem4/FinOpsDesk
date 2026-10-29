using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.BankTransactions;

public sealed class BankTransactionTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");
    private static readonly DateOnly BookingDate = new(2026, 8, 24);

    [Fact]
    public void New_transaction_should_be_fully_unapplied()
    {
        var transaction = CreateTransaction(1000m);

        Assert.Equal(TestOrganizationId, transaction.OrganizationId);
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

        Result result = transaction.Allocate(
            new Money(300m, Pln));

        Assert.True(result.IsSuccess);
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

        Result result = transaction.Allocate(
            new Money(1001m, Pln));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code,
            result.Error?.Code);

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

        Result result = transaction.Allocate(
            new Money(100m, Eur));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.CurrencyMismatch.Code,
            result.Error?.Code);
    }

    [Fact]
    public void CanReverseAllocation_should_not_change_allocated_amount()
    {
        var transaction = CreateTransaction(1000m);
        transaction.Allocate(new Money(300m, Pln));

        Result accepted = transaction.CanReverseAllocation(new Money(300m, Pln));
        Result rejected = transaction.CanReverseAllocation(new Money(301m, Pln));

        Assert.True(accepted.IsSuccess);
        Assert.Equal(
            ReconciliationErrors.BankTransactionReversalExceedsAllocated.Code,
            rejected.Error?.Code);
        Assert.Equal(new Money(300m, Pln), transaction.AllocatedAmount);
    }

    [Fact]
    public void ReverseAllocation_should_restore_unapplied_amount()
    {
        var transaction = CreateTransaction(1000m);

        transaction.Allocate(
            new Money(600m, Pln));

        Result result = transaction.ReverseAllocation(
            new Money(200m, Pln));

        Assert.True(result.IsSuccess);

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

        Result result = transaction.ReverseAllocation(
            new Money(301m, Pln));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.BankTransactionReversalExceedsAllocated.Code,
            result.Error?.Code);

        Assert.Equal(
            new Money(300m, Pln),
            transaction.AllocatedAmount);
    }

    [Fact]
    public void ReverseAllocation_should_reject_different_currency()
    {
        var transaction = CreateTransaction(1000m);

        transaction.Allocate(
            new Money(300m, Pln));

        Result result = transaction.ReverseAllocation(
            new Money(100m, Eur));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.CurrencyMismatch.Code,
            result.Error?.Code);

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
                TestOrganizationId,
                new Money(amount, Pln),
                BookingDate,
                "INV-001"));
    }

    [Fact]
    public void Constructor_should_trim_reference()
    {
        var transaction = CreateTransaction(
            1000m,
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
        var transaction = CreateTransaction(1000m, reference);

        Assert.Null(transaction.Reference);
    }

    private static BankTransaction CreateTransaction(
        decimal amount,
        string? reference = "INV-001")
    {
        return new BankTransaction(
            BankTransactionId.New(),
            TestOrganizationId,
            new Money(amount, Pln),
            BookingDate,
            reference);
    }
}