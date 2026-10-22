using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices", table =>
        {
            table.HasCheckConstraint(
                "CK_Invoices_SourceIdentity_NullPair",
                SourceIdentityPropertyConfiguration.NullPairConstraintSql);
        });

        builder.HasKey(invoice => invoice.Id);

        builder.Property(invoice => invoice.Id)
            .HasConversion(ReconciliationValueConverters.InvoiceId)
            .ValueGeneratedNever();

        builder.Property(invoice => invoice.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.HasAlternateKey(invoice => new
        {
            invoice.OrganizationId,
            invoice.Id
        });

        builder.Property(invoice => invoice.InvoiceNumber)
            .IsRequired();

        MoneyPropertyConfiguration.Configure(
            builder.ComplexProperty(invoice => invoice.TotalAmount),
            "TotalAmount",
            "TotalCurrency");

        MoneyPropertyConfiguration.Configure(
            builder.ComplexProperty(invoice => invoice.AllocatedAmount),
            "AllocatedAmount",
            "AllocatedCurrency");

        builder.Ignore(invoice => invoice.OutstandingAmount);

        SourceIdentityPropertyConfiguration.Configure(
            builder.ComplexProperty(invoice => invoice.SourceIdentity));

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion()
            .IsRequired();
    }
}