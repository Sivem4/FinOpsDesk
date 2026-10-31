using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.E2ETests;

internal sealed class E2EBooks
{
    public required IReadOnlyList<Invoice> Invoices { get; init; }

    public required IReadOnlyList<BankTransaction> BankTransactions { get; init; }

    public required IReadOnlyList<Allocation> Allocations { get; init; }

    public required IReadOnlyList<FinancialAuditEntry> Audit { get; init; }
}

internal static class E2EFinance
{
    public static async Task<Guid> ConfirmedAllocationIdAsync(
        string connectionString,
        Guid organizationId,
        Guid invoiceId,
        Guid bankTransactionId)
    {
        E2EBooks books = await ReadAsync(connectionString, organizationId);
        Allocation allocation = Assert.Single(
            books.Allocations,
            item => item.InvoiceId.Value == invoiceId && item.BankTransactionId.Value == bankTransactionId);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Equal(E2EWorkspace.OpenAmount, allocation.Amount.Amount);
        Assert.Equal("PLN", allocation.Amount.Currency.Value);
        return allocation.Id.Value;
    }

    public static async Task<bool> IsAllocationReversedAsync(
        string connectionString,
        Guid organizationId,
        Guid allocationId)
    {
        E2EBooks books = await ReadAsync(connectionString, organizationId);
        Allocation? allocation = books.Allocations.SingleOrDefault(item => item.Id.Value == allocationId);
        return allocation is not null && allocation.Status == AllocationStatus.Reversed;
    }

    public static async Task<bool> HasPartialAllocationAsync(
        string connectionString,
        Guid organizationId,
        Guid invoiceId,
        decimal allocated)
    {
        E2EBooks books = await ReadAsync(connectionString, organizationId);
        Invoice? invoice = books.Invoices.SingleOrDefault(item => item.Id.Value == invoiceId);
        if (invoice is null || invoice.AllocatedAmount.Amount != allocated)
        {
            return false;
        }

        return books.Allocations.Count(item =>
            item.InvoiceId.Value == invoiceId && item.Status == AllocationStatus.Confirmed) == 1;
    }

    public static async Task RequireReversedAsync(
        string connectionString,
        Guid organizationId,
        Guid allocationId,
        decimal originalAmount,
        string reason,
        int? allocationCount = null)
    {
        E2EBooks books = await ReadAsync(connectionString, organizationId);
        if (allocationCount is int count)
        {
            Assert.Equal(count, books.Allocations.Count);
        }

        Allocation allocation = Assert.Single(books.Allocations, item => item.Id.Value == allocationId);
        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(originalAmount, allocation.Amount.Amount);
        Assert.Equal("PLN", allocation.Amount.Currency.Value);
        Assert.Equal(reason, allocation.ReversalReason);
        Assert.Single(books.Allocations, item => item.InvoiceId.Value == allocation.InvoiceId.Value);

        Invoice invoice = Assert.Single(books.Invoices, item => item.Id == allocation.InvoiceId);
        Assert.Equal(originalAmount, invoice.TotalAmount.Amount);
        Assert.Equal(0m, invoice.AllocatedAmount.Amount);
        Assert.Equal(originalAmount, invoice.OutstandingAmount.Amount);
        Assert.Equal("PLN", invoice.TotalAmount.Currency.Value);

        BankTransaction bankTransaction = Assert.Single(
            books.BankTransactions,
            item => item.Id == allocation.BankTransactionId);
        Assert.Equal(originalAmount, bankTransaction.Amount.Amount);
        Assert.Equal(0m, bankTransaction.AllocatedAmount.Amount);
        Assert.Equal(originalAmount, bankTransaction.UnappliedAmount.Amount);
        Assert.Equal("PLN", bankTransaction.Amount.Currency.Value);

        List<FinancialAuditEntry> entries = books.Audit
            .Where(item => item.AllocationId == allocation.Id)
            .ToList();
        Assert.Equal(2, entries.Count);
        FinancialAuditEntry confirmation = Assert.Single(
            entries,
            item => item.Action == FinancialAuditAction.Confirmation);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, confirmation.IdempotencyOperation);
        Assert.Equal(E2EAccessToken.ActorId, confirmation.ActorId);
        Assert.Equal(originalAmount, confirmation.Amount.Amount);
        Assert.Null(confirmation.Reason);
        FinancialAuditEntry reversal = Assert.Single(
            entries,
            item => item.Action == FinancialAuditAction.Reversal);
        Assert.Equal(AllocationIdempotencyOperation.Reversal, reversal.IdempotencyOperation);
        Assert.Equal(E2EAccessToken.ActorId, reversal.ActorId);
        Assert.Equal(originalAmount, reversal.Amount.Amount);
        Assert.Equal("PLN", reversal.Amount.Currency.Value);
        Assert.Equal(reason, reversal.Reason);
    }

    public static async Task RequirePartialAsync(
        string connectionString,
        Guid organizationId,
        Guid allocationId,
        Guid invoiceId,
        Guid bankTransactionId,
        int? allocationCount = null)
    {
        E2EBooks books = await ReadAsync(connectionString, organizationId);
        if (allocationCount is int count)
        {
            Assert.Equal(count, books.Allocations.Count);
        }

        Allocation allocation = Assert.Single(books.Allocations, item => item.Id.Value == allocationId);
        Assert.Equal(invoiceId, allocation.InvoiceId.Value);
        Assert.Equal(bankTransactionId, allocation.BankTransactionId.Value);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Equal(E2EWorkspace.PartialAmount, allocation.Amount.Amount);
        Assert.Equal("PLN", allocation.Amount.Currency.Value);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, allocation.IdempotencyOperation);
        Assert.Null(allocation.ReversalReason);
        Assert.Single(books.Allocations, item => item.InvoiceId.Value == invoiceId);

        Invoice invoice = Assert.Single(books.Invoices, item => item.Id.Value == invoiceId);
        Assert.Equal(E2EWorkspace.OpenAmount, invoice.TotalAmount.Amount);
        Assert.Equal(E2EWorkspace.PartialAmount, invoice.AllocatedAmount.Amount);
        Assert.Equal(E2EWorkspace.RemainingAmount, invoice.OutstandingAmount.Amount);

        BankTransaction bankTransaction = Assert.Single(
            books.BankTransactions,
            item => item.Id.Value == bankTransactionId);
        Assert.Equal(E2EWorkspace.OpenAmount, bankTransaction.Amount.Amount);
        Assert.Equal(E2EWorkspace.PartialAmount, bankTransaction.AllocatedAmount.Amount);
        Assert.Equal(E2EWorkspace.RemainingAmount, bankTransaction.UnappliedAmount.Amount);

        List<FinancialAuditEntry> entries = books.Audit
            .Where(item => item.AllocationId == allocation.Id)
            .ToList();
        FinancialAuditEntry confirmation = Assert.Single(entries);
        Assert.Equal(FinancialAuditAction.Confirmation, confirmation.Action);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, confirmation.IdempotencyOperation);
        Assert.Equal(E2EAccessToken.ActorId, confirmation.ActorId);
        Assert.Equal(E2EWorkspace.PartialAmount, confirmation.Amount.Amount);
        Assert.Equal("PLN", confirmation.Amount.Currency.Value);
        Assert.Null(confirmation.Reason);
    }

    public static async Task RequireNoFinancialRowsAsync(string connectionString, Guid organizationId)
    {
        E2EBooks books = await ReadAsync(connectionString, organizationId);
        Assert.Empty(books.Invoices);
        Assert.Empty(books.BankTransactions);
        Assert.Empty(books.Allocations);
        Assert.Empty(books.Audit);
    }

    private static async Task<E2EBooks> ReadAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        return new E2EBooks
        {
            Invoices = await context.Invoices.AsNoTracking()
                .Where(invoice => invoice.OrganizationId == tenant)
                .ToListAsync(),
            BankTransactions = await context.BankTransactions.AsNoTracking()
                .Where(transaction => transaction.OrganizationId == tenant)
                .ToListAsync(),
            Allocations = await context.Allocations.AsNoTracking()
                .Where(allocation => allocation.OrganizationId == tenant)
                .ToListAsync(),
            Audit = await context.FinancialAuditEntries.AsNoTracking()
                .Where(entry => entry.OrganizationId == tenant)
                .ToListAsync(),
        };
    }
}