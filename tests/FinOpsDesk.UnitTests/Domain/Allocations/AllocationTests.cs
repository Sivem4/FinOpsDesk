using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Allocations;

public sealed class AllocationTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
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
            TestOrganizationId,
            invoiceId,
            bankTransactionId,
            amount,
            ConfirmedAt);

        Assert.Equal(id, allocation.Id);
        Assert.Equal(TestOrganizationId, allocation.OrganizationId);
        Assert.Equal(invoiceId, allocation.InvoiceId);
        Assert.Equal(bankTransactionId, allocation.BankTransactionId);
        Assert.Equal(amount, allocation.Amount);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Equal(ConfirmedAt, allocation.ConfirmedAt);
        Assert.Null(allocation.IdempotencyKey);
        Assert.Null(allocation.IdempotencyOperation);
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
                TestOrganizationId,
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
                TestOrganizationId,
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

        Result result = allocation.Reverse("Duplicate payment", reversedAt);

        Assert.True(result.IsSuccess);
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

        Result result = allocation.Reverse(
            "Entered in error",
            reversedAt.AddHours(1));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.AllocationAlreadyReversed.Code,
            result.Error?.Code);

        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(reversedAt, allocation.ReversedAt);
        Assert.Equal("Duplicate payment", allocation.ReversalReason);
    }

    [Fact]
    public void Reverse_should_reject_null_reason_when_already_reversed()
    {
        var allocation = CreateAllocation();
        var reversedAt = ConfirmedAt.AddHours(1);

        allocation.Reverse("Duplicate payment", reversedAt);

        Assert.Throws<ArgumentNullException>(
            () => allocation.Reverse(
                null!,
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

    [Fact]
    public void New_allocation_should_keep_a_non_empty_idempotency_key()
    {
        Guid idempotencyKey = Guid.NewGuid();

        var allocation = new Allocation(
            AllocationId.New(),
            TestOrganizationId,
            InvoiceId.New(),
            BankTransactionId.New(),
            new Money(300m, Pln),
            ConfirmedAt,
            idempotencyKey,
            AllocationIdempotencyOperation.ExplicitAmount);

        Assert.Equal(idempotencyKey, allocation.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, allocation.IdempotencyOperation);
    }

    [Fact]
    public void New_allocation_should_reject_a_key_without_an_operation()
    {
        Assert.Throws<ArgumentException>(
            () => new Allocation(
                AllocationId.New(),
                TestOrganizationId,
                InvoiceId.New(),
                BankTransactionId.New(),
                new Money(300m, Pln),
                ConfirmedAt,
                Guid.NewGuid()));
    }

    [Fact]
    public void New_allocation_should_reject_an_operation_without_a_key()
    {
        Assert.Throws<ArgumentException>(
            () => new Allocation(
                AllocationId.New(),
                TestOrganizationId,
                InvoiceId.New(),
                BankTransactionId.New(),
                new Money(300m, Pln),
                ConfirmedAt,
                idempotencyOperation: AllocationIdempotencyOperation.ExplicitAmount));
    }

    [Fact]
    public void New_allocation_should_reject_an_undefined_operation()
    {
        Assert.Throws<ArgumentException>(
            () => new Allocation(
                AllocationId.New(),
                TestOrganizationId,
                InvoiceId.New(),
                BankTransactionId.New(),
                new Money(300m, Pln),
                ConfirmedAt,
                Guid.NewGuid(),
                (AllocationIdempotencyOperation)99));
    }

    [Fact]
    public void New_allocation_should_reject_an_empty_idempotency_key()
    {
        Assert.Throws<ArgumentException>(
            () => new Allocation(
                AllocationId.New(),
                TestOrganizationId,
                InvoiceId.New(),
                BankTransactionId.New(),
                new Money(300m, Pln),
                ConfirmedAt,
                Guid.Empty));
    }

    private static Allocation CreateAllocation()
    {
        return new Allocation(
            AllocationId.New(),
            TestOrganizationId,
            InvoiceId.New(),
            BankTransactionId.New(),
            new Money(300m, Pln),
            ConfirmedAt);
    }
}