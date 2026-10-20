using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Invoices;

public sealed class InvoiceSourceIdentityTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly SourceIdentity ImportedIdentity = new(
        new SourceSystem("erp"),
        new ExternalId("INV-001"));

    [Fact]
    public void Manual_invoice_should_have_no_source_identity()
    {
        var invoice = new Invoice(
            InvoiceId.New(),
            TestOrganizationId,
            " INV-001 ",
            new Money(1000m, Pln));

        Assert.Null(invoice.SourceIdentity);
        Assert.Equal("INV-001", invoice.InvoiceNumber);
        Assert.Equal(new Money(1000m, Pln), invoice.OutstandingAmount);
        Assert.Equal(Money.Zero(Pln), invoice.AllocatedAmount);
    }

    [Fact]
    public void Imported_invoice_should_retain_source_identity()
    {
        var invoice = CreateImportedInvoice(1000m);

        Assert.Equal(ImportedIdentity, invoice.SourceIdentity);
        Assert.Equal(new Money(1000m, Pln), invoice.TotalAmount);
    }

    [Fact]
    public void Imported_constructor_should_reject_null_source_identity()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Invoice(
                InvoiceId.New(),
                TestOrganizationId,
                "INV-001",
                new Money(1000m, Pln),
                null!));
    }

    [Fact]
    public void Equivalent_imported_business_data_should_match_normalized_invoice_number_and_total()
    {
        var invoice = CreateImportedInvoice(1000.50m);

        Assert.True(
            invoice.HasEquivalentImportedBusinessData(
                "  INV-001  ",
                new Money(1000.50m, Pln)));
        Assert.True(
            invoice.HasEquivalentImportedBusinessData(
                "INV-001",
                new Money(1000.500m, Pln)));
    }

    [Theory]
    [InlineData("INV-002", 1000, "PLN")]
    [InlineData("inv-001", 1000, "PLN")]
    [InlineData("INV-001", 999, "PLN")]
    [InlineData("INV-001", 1000, "EUR")]
    public void Conflicting_imported_business_data_should_not_match(
        string invoiceNumber,
        int amount,
        string currency)
    {
        var invoice = CreateImportedInvoice(1000m);

        Assert.False(
            invoice.HasEquivalentImportedBusinessData(
                invoiceNumber,
                new Money(amount, new CurrencyCode(currency))));
        Assert.Equal(Money.Zero(Pln), invoice.AllocatedAmount);
    }

    [Fact]
    public void Equivalence_should_ignore_allocated_amount()
    {
        var invoice = CreateImportedInvoice(1000m);

        invoice.Allocate(new Money(400m, Pln));

        Assert.True(
            invoice.HasEquivalentImportedBusinessData(
                "INV-001",
                new Money(1000m, Pln)));
        Assert.False(
            invoice.HasEquivalentImportedBusinessData(
                "INV-001",
                invoice.OutstandingAmount));
        Assert.Equal(new Money(400m, Pln), invoice.AllocatedAmount);
    }

    private static Invoice CreateImportedInvoice(decimal amount)
    {
        return new Invoice(
            InvoiceId.New(),
            TestOrganizationId,
            "INV-001",
            new Money(amount, Pln),
            ImportedIdentity);
    }
}