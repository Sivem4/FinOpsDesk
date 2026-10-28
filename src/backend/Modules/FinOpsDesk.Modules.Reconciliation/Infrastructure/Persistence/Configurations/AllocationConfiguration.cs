using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class AllocationConfiguration : IEntityTypeConfiguration<Allocation>
{
    internal const string IdempotencyIndexName = "UX_Allocations_OrganizationId_IdempotencyKey";

    internal const string IdempotencyOperationCheckName = "CK_Allocations_IdempotencyOperation";

    internal const string IdempotencyOperationCheckSql =
        "([IdempotencyKey] IS NULL AND [IdempotencyOperation] IS NULL) OR ([IdempotencyKey] IS NOT NULL AND [IdempotencyOperation] IS NOT NULL AND [IdempotencyOperation] IN (1, 2, 3))";

    public void Configure(EntityTypeBuilder<Allocation> builder)
    {
        builder.ToTable("Allocations", table =>
        {
            table.HasCheckConstraint(
                IdempotencyOperationCheckName,
                IdempotencyOperationCheckSql);
        });

        builder.HasKey(allocation => allocation.Id);

        builder.HasAlternateKey(allocation => new
        {
            allocation.OrganizationId,
            allocation.Id,
        });

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

        builder.Property(allocation => allocation.IdempotencyOperation);

        builder.HasIndex(allocation => new
        {
            allocation.OrganizationId,
            allocation.IdempotencyKey,
        })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL")
            .HasDatabaseName(IdempotencyIndexName);

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