using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class BankTransactionConfiguration : IEntityTypeConfiguration<BankTransaction>
{
    public void Configure(EntityTypeBuilder<BankTransaction> builder)
    {
        builder.ToTable("BankTransactions", table =>
        {
            table.HasCheckConstraint(
                "CK_BankTransactions_SourceIdentity_NullPair",
                SourceIdentityPropertyConfiguration.NullPairConstraintSql);
        });

        builder.HasKey(bankTransaction => bankTransaction.Id);

        builder.Property(bankTransaction => bankTransaction.Id)
            .HasConversion(ReconciliationValueConverters.BankTransactionId)
            .ValueGeneratedNever();

        builder.Property(bankTransaction => bankTransaction.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.HasAlternateKey(bankTransaction => new
        {
            bankTransaction.OrganizationId,
            bankTransaction.Id
        });

        builder.Property(bankTransaction => bankTransaction.Reference);

        builder.Property(bankTransaction => bankTransaction.BookingDate)
            .HasColumnType("date");

        MoneyPropertyConfiguration.Configure(
            builder.ComplexProperty(bankTransaction => bankTransaction.Amount),
            "Amount",
            "Currency");

        MoneyPropertyConfiguration.Configure(
            builder.ComplexProperty(bankTransaction => bankTransaction.AllocatedAmount),
            "AllocatedAmount",
            "AllocatedCurrency");

        builder.Ignore(bankTransaction => bankTransaction.UnappliedAmount);

        SourceIdentityPropertyConfiguration.Configure(
            builder.ComplexProperty(bankTransaction => bankTransaction.SourceIdentity));

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion()
            .IsRequired();
    }
}