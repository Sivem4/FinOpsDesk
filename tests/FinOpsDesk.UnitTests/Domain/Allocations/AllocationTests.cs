using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Allocations;

public sealed class AllocationTests
{
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_allocation_should_be_confirmed()
    {
        var id = AllocationId.New();
        var invoiceId = InvoiceId.New();
        var bankTransactionId = BankTransactionId.New();
        var amount = new Money(300m, Pln);

        var allocation = new Allocation(
            id,
            invoiceId,
            bankTransactionId,
            amount,
            ConfirmedAt);

        Assert.Equal(id, allocation.Id);
        Assert.Equal(invoiceId, allocation.InvoiceId);
        Assert.Equal(bankTransactionId, allocation.BankTransactionId);
        Assert.Equal(amount, allocation.Amount);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Equal(ConfirmedAt, allocation.ConfirmedAt);
        Assert.Null(allocation.ReversedAt);
        Assert.Null(allocation.ReversalReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Constructor_should_reject_non_positive_amount(
        decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Allocation(
                AllocationId.New(),
                InvoiceId.New(),
                BankTransactionId.New(),
                new Money(amount, Pln),
                ConfirmedAt));
    }

    [Fact]
    public void Constructor_should_reject_default_confirmed_at()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Allocation(
                AllocationId.New(),
                InvoiceId.New(),
                BankTransactionId.New(),
                new Money(300m, Pln),
                default));
    }

    [Fact]
    public void Reverse_should_mark_allocation_as_reversed()
    {
        var allocation = CreateAllocation();
        var reversedAt = ConfirmedAt.AddHours(1);

        allocation.Reverse("Duplicate payment", reversedAt);

        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(reversedAt, allocation.ReversedAt);
        Assert.Equal("Duplicate payment", allocation.ReversalReason);
        Assert.Equal(
            new Money(300m, Pln),
            allocation.Amount);
    }

    [Fact]
    public void Reverse_should_trim_reason()
    {
        var allocation = CreateAllocation();

        allocation.Reverse(
            "  Duplicate payment  ",
            ConfirmedAt.AddHours(1));

        Assert.Equal(
            "Duplicate payment",
            allocation.ReversalReason);
    }

    [Fact]
    public void Reverse_should_allow_same_time_as_confirmation()
    {
        var allocation = CreateAllocation();

        allocation.Reverse("Duplicate payment", ConfirmedAt);

        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(ConfirmedAt, allocation.ReversedAt);
    }

    [Fact]
    public void Reverse_should_reject_when_already_reversed()
    {
        var allocation = CreateAllocation();
        var reversedAt = ConfirmedAt.AddHours(1);

        allocation.Reverse("Duplicate payment", reversedAt);

        Assert.Throws<InvalidOperationException>(
            () => allocation.Reverse(
                "Entered in error",
                reversedAt.AddHours(1)));

        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(reversedAt, allocation.ReversedAt);
        Assert.Equal("Duplicate payment", allocation.ReversalReason);
    }

    [Fact]
    public void Reverse_should_reject_null_reason()
    {
        var allocation = CreateAllocation();

        Assert.Throws<ArgumentNullException>(
            () => allocation.Reverse(
                null!,
                ConfirmedAt.AddHours(1)));

        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversedAt);
        Assert.Null(allocation.ReversalReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reverse_should_reject_empty_or_whitespace_reason(
        string reason)
    {
        var allocation = CreateAllocation();

        Assert.Throws<ArgumentException>(
            () => allocation.Reverse(
                reason,
                ConfirmedAt.AddHours(1)));

        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversedAt);
        Assert.Null(allocation.ReversalReason);
    }

    [Fact]
    public void Reverse_should_reject_time_before_confirmation()
    {
        var allocation = CreateAllocation();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => allocation.Reverse(
                "Duplicate payment",
                ConfirmedAt.AddMinutes(-1)));

        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversedAt);
        Assert.Null(allocation.ReversalReason);
    }

    private static Allocation CreateAllocation()
    {
        return new Allocation(
            AllocationId.New(),
            InvoiceId.New(),
            BankTransactionId.New(),
            new Money(300m, Pln),
            ConfirmedAt);
    }
}