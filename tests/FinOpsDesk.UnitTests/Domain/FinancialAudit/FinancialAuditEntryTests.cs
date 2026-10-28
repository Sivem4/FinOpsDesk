using System.Reflection;

using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.FinancialAudit;

public sealed class FinancialAuditEntryTests
{
    private static readonly OrganizationId Organization = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly CurrencyCode Pln = new("PLN");

    private static readonly DateTimeOffset ConfirmedAt = new(2026, 10, 28, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ForConfirmation_copies_identity_amount_and_confirmation_time_from_the_allocation()
    {
        Guid idempotencyKey = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Allocation allocation = CreateAllocation(
            AllocationIdempotencyOperation.ExactMatch,
            idempotencyKey,
            42.5m);
        var auditId = FinancialAuditEntryId.New();

        FinancialAuditEntry entry = FinancialAuditEntry.ForConfirmation(
            auditId,
            allocation,
            "  operator-a  ",
            batchExecutionId: null);

        Assert.Equal(auditId, entry.Id);
        Assert.Equal(allocation.OrganizationId, entry.OrganizationId);
        Assert.Equal(allocation.Id, entry.AllocationId);
        Assert.Equal(allocation.InvoiceId, entry.InvoiceId);
        Assert.Equal(allocation.BankTransactionId, entry.BankTransactionId);
        Assert.Null(entry.BatchExecutionId);
        Assert.Equal(FinancialAuditAction.Confirmation, entry.Action);
        Assert.Equal("operator-a", entry.ActorId);
        Assert.Equal(allocation.Amount, entry.Amount);
        Assert.Equal(allocation.ConfirmedAt, entry.RecordedAt);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, entry.IdempotencyOperation);
        Assert.Equal(idempotencyKey, entry.IdempotencyKey);
        Assert.Empty(typeof(FinancialAuditEntry).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void ForConfirmation_preserves_explicit_amount_operation_metadata()
    {
        Guid idempotencyKey = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Allocation allocation = CreateAllocation(
            AllocationIdempotencyOperation.ExplicitAmount,
            idempotencyKey,
            10m);

        FinancialAuditEntry entry = FinancialAuditEntry.ForConfirmation(
            FinancialAuditEntryId.New(),
            allocation,
            "operator-b",
            batchExecutionId: null);

        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, entry.IdempotencyOperation);
        Assert.Equal(idempotencyKey, entry.IdempotencyKey);
        Assert.NotEqual(AllocationIdempotencyOperation.ExactMatch, entry.IdempotencyOperation);
    }

    [Fact]
    public void ForConfirmation_requires_a_batch_execution_for_batch_exact_match()
    {
        Allocation allocation = CreateAllocation(
            AllocationIdempotencyOperation.BatchExactMatch,
            Guid.NewGuid(),
            10m);
        var batchExecutionId = BatchExecutionId.New();

        ArgumentNullException missing = Assert.Throws<ArgumentNullException>(
            () => FinancialAuditEntry.ForConfirmation(
                FinancialAuditEntryId.New(),
                allocation,
                "operator-a",
                batchExecutionId: null));
        FinancialAuditEntry entry = FinancialAuditEntry.ForConfirmation(
            FinancialAuditEntryId.New(),
            allocation,
            "operator-a",
            batchExecutionId);

        Assert.Equal(nameof(batchExecutionId), missing.ParamName);
        Assert.Equal(batchExecutionId, entry.BatchExecutionId);
        Assert.Equal(AllocationIdempotencyOperation.BatchExactMatch, entry.IdempotencyOperation);
    }

    [Theory]
    [InlineData(AllocationIdempotencyOperation.ExactMatch)]
    [InlineData(AllocationIdempotencyOperation.ExplicitAmount)]
    public void ForConfirmation_rejects_a_batch_execution_for_a_non_batch_operation(
        AllocationIdempotencyOperation operation)
    {
        Allocation allocation = CreateAllocation(operation, Guid.NewGuid(), 10m);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => FinancialAuditEntry.ForConfirmation(
                FinancialAuditEntryId.New(),
                allocation,
                "operator-a",
                BatchExecutionId.New()));

        Assert.Equal("batchExecutionId", exception.ParamName);
    }

    [Fact]
    public void ForConfirmation_rejects_an_allocation_without_command_identity()
    {
        var allocation = new Allocation(
            AllocationId.New(),
            Organization,
            InvoiceId.New(),
            BankTransactionId.New(),
            new Money(10m, Pln),
            ConfirmedAt);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => FinancialAuditEntry.ForConfirmation(
                FinancialAuditEntryId.New(),
                allocation,
                "operator-a",
                batchExecutionId: null));

        Assert.Equal("allocation", exception.ParamName);
    }

    [Fact]
    public void ForConfirmation_rejects_a_reversed_allocation()
    {
        Allocation allocation = CreateAllocation(
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid(),
            10m);
        allocation.Reverse("duplicate payment", ConfirmedAt.AddMinutes(5));
        Assert.Equal(AllocationStatus.Reversed, allocation.Status);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => FinancialAuditEntry.ForConfirmation(
                FinancialAuditEntryId.New(),
                allocation,
                "operator-a",
                batchExecutionId: null));

        Assert.Contains("currently confirmed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForConfirmation_rejects_a_blank_or_overlong_actor()
    {
        Allocation allocation = CreateAllocation(
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid(),
            10m);

        Assert.Throws<ArgumentNullException>(
            () => FinancialAuditEntry.ForConfirmation(
                FinancialAuditEntryId.New(),
                allocation,
                null!,
                batchExecutionId: null));
        Assert.Throws<ArgumentException>(
            () => FinancialAuditEntry.ForConfirmation(
                FinancialAuditEntryId.New(),
                allocation,
                "   ",
                batchExecutionId: null));
        Assert.Throws<ArgumentException>(
            () => FinancialAuditEntry.ForConfirmation(
                FinancialAuditEntryId.New(),
                allocation,
                new string('a', FinancialAuditEntry.MaxActorIdLength + 1),
                batchExecutionId: null));
    }

    private static Allocation CreateAllocation(
        AllocationIdempotencyOperation operation,
        Guid idempotencyKey,
        decimal amount)
    {
        return new Allocation(
            AllocationId.New(),
            Organization,
            InvoiceId.New(),
            BankTransactionId.New(),
            new Money(amount, Pln),
            ConfirmedAt,
            idempotencyKey,
            operation);
    }
}