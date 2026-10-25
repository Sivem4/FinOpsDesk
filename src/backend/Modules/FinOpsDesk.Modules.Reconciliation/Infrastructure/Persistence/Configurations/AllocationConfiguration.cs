using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class AllocationConfiguration : IEntityTypeConfiguration<Allocation>
{
    public void Configure(EntityTypeBuilder<Allocation> builder)
    {
        builder.ToTable("Allocations");

        builder.HasKey(allocation => allocation.Id);

        builder.Property(allocation => allocation.Id)
            .HasConversion(ReconciliationValueConverters.AllocationId)
            .ValueGeneratedNever();

        builder.Property(allocation => allocation.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(allocation => allocation.InvoiceId)
            .HasConversion(ReconciliationValueConverters.InvoiceId);

        builder.Property(allocation => allocation.BankTransactionId)
            .HasConversion(ReconciliationValueConverters.BankTransactionId);

        builder.Property(allocation => allocation.Status);

        builder.Property(allocation => allocation.ConfirmedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(allocation => allocation.IdempotencyKey);

        builder.HasIndex(allocation => new
        {
            allocation.OrganizationId,
            allocation.IdempotencyKey,
        })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL")
            .HasDatabaseName("UX_Allocations_OrganizationId_IdempotencyKey");

        builder.Property(allocation => allocation.ReversedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(allocation => allocation.ReversalReason);

        MoneyPropertyConfiguration.Configure(
            builder.ComplexProperty(allocation => allocation.Amount),
            "Amount",
            "Currency");

        builder.HasOne<Invoice>()
            .WithMany()
            .HasForeignKey(allocation => new
            {
                allocation.OrganizationId,
                allocation.InvoiceId
            })
            .HasPrincipalKey(invoice => new
            {
                invoice.OrganizationId,
                invoice.Id
            })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BankTransaction>()
            .WithMany()
            .HasForeignKey(allocation => new
            {
                allocation.OrganizationId,
                allocation.BankTransactionId
            })
            .HasPrincipalKey(bankTransaction => new
            {
                bankTransaction.OrganizationId,
                bankTransaction.Id
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}