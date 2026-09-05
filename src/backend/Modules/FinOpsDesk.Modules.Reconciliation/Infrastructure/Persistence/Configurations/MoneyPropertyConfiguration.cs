using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal static class MoneyPropertyConfiguration
{
    public static void Configure(
        ComplexPropertyBuilder<Money> builder,
        string amountColumnName,
        string currencyColumnName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(amountColumnName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyColumnName);

        builder.IsRequired();

        builder.Property(money => money.Amount)
            .HasColumnName(amountColumnName)
            .HasPrecision(19, 4);

        builder.Property(money => money.Currency)
            .HasColumnName(currencyColumnName)
            .HasConversion(ReconciliationValueConverters.CurrencyCode)
            .HasMaxLength(3)
            .IsUnicode(false)
            .IsFixedLength();
    }
}