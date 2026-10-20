using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.BankTransactions;

public sealed class BankTransactionSourceIdentityTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly DateOnly BookingDate = new(2026, 8, 24);
    private static readonly SourceIdentity ImportedIdentity = new(
        new SourceSystem("mbank:pl123"),
        new ExternalId("TX-001"));

    [Fact]
    public void Manual_transaction_should_have_no_source_identity()
    {
        var transaction = new BankTransaction(
            BankTransactionId.New(),
            TestOrganizationId,
            new Money(1000m, Pln),
            BookingDate,
            "  INV-001  ");

        Assert.Null(transaction.SourceIdentity);
        Assert.Equal("INV-001", transaction.Reference);
        Assert.Equal(new Money(1000m, Pln), transaction.UnappliedAmount);
        Assert.Equal(Money.Zero(Pln), transaction.AllocatedAmount);
    }

    [Fact]
    public void Imported_transaction_should_retain_source_identity()
    {
        var transaction = CreateImportedTransaction(1000m, "INV-001");

        Assert.Equal(ImportedIdentity, transaction.SourceIdentity);
        Assert.Equal(BookingDate, transaction.BookingDate);
    }

    [Fact]
    public void Imported_constructor_should_reject_null_source_identity()
    {
        Assert.Throws<ArgumentNullException>(
            () => new BankTransaction(
                BankTransactionId.New(),
                TestOrganizationId,
                new Money(1000m, Pln),
                BookingDate,
                "INV-001",
                null!));
    }

    [Fact]
    public void Equivalent_imported_business_data_should_match_amount_date_and_reference()
    {
        var transaction = CreateImportedTransaction(250.10m, "INV-001");

        Assert.True(
            transaction.HasEquivalentImportedBusinessData(
                new Money(250.10m, Pln),
                BookingDate,
                "  INV-001  "));
        Assert.True(
            transaction.HasEquivalentImportedBusinessData(
                new Money(250.100m, Pln),
                BookingDate,
                "INV-001"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_reference_should_match_a_missing_reference(string? reference)
    {
        var transaction = CreateImportedTransaction(1000m, reference: null);

        Assert.True(
            transaction.HasEquivalentImportedBusinessData(
                new Money(1000m, Pln),
                BookingDate,
                reference));
    }

    [Fact]
    public void Conflicting_imported_business_data_should_not_match()
    {
        var transaction = CreateImportedTransaction(1000m, "INV-001");

        Assert.False(
            transaction.HasEquivalentImportedBusinessData(
                new Money(1000m, new CurrencyCode("EUR")),
                BookingDate,
                "INV-001"));
        Assert.False(
            transaction.HasEquivalentImportedBusinessData(
                new Money(999m, Pln),
                BookingDate,
                "INV-001"));
        Assert.False(
            transaction.HasEquivalentImportedBusinessData(
                new Money(1000m, Pln),
                BookingDate.AddDays(1),
                "INV-001"));
        Assert.False(
            transaction.HasEquivalentImportedBusinessData(
                new Money(1000m, Pln),
                BookingDate,
                "inv-001"));
        Assert.Equal(Money.Zero(Pln), transaction.AllocatedAmount);
    }

    [Fact]
    public void Equivalence_should_ignore_allocated_amount()
    {
        var transaction = CreateImportedTransaction(1000m, "INV-001");

        transaction.Allocate(new Money(400m, Pln));

        Assert.True(
            transaction.HasEquivalentImportedBusinessData(
                new Money(1000m, Pln),
                BookingDate,
                "INV-001"));
        Assert.False(
            transaction.HasEquivalentImportedBusinessData(
                transaction.UnappliedAmount,
                BookingDate,
                "INV-001"));
        Assert.Equal(new Money(400m, Pln), transaction.AllocatedAmount);
    }

    private static BankTransaction CreateImportedTransaction(
        decimal amount,
        string? reference)
    {
        return new BankTransaction(
            BankTransactionId.New(),
            TestOrganizationId,
            new Money(amount, Pln),
            BookingDate,
            reference,
            ImportedIdentity);
    }
}