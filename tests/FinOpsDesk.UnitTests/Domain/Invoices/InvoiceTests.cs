using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Invoices;

public sealed class InvoiceTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");

    [Fact]
    public void New_invoice_should_be_fully_outstanding()
    {
        var invoice = CreateInvoice(1000m);

        Assert.Equal(TestOrganizationId, invoice.OrganizationId);
        Assert.Equal(
            new Money(0m, Pln),
            invoice.AllocatedAmount);

        Assert.Equal(
            new Money(1000m, Pln),
            invoice.OutstandingAmount);
    }

    [Fact]
    public void Allocate_should_reduce_outstanding_amount()
    {
        var invoice = CreateInvoice(1000m);

        Result result = invoice.Allocate(new Money(300m, Pln));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new Money(300m, Pln),
            invoice.AllocatedAmount);

        Assert.Equal(
            new Money(700m, Pln),
            invoice.OutstandingAmount);
    }

    [Fact]
    public void Multiple_allocations_should_accumulate()
    {
        var invoice = CreateInvoice(1000m);

        invoice.Allocate(new Money(300m, Pln));
        invoice.Allocate(new Money(200m, Pln));

        Assert.Equal(
            new Money(500m, Pln),
            invoice.AllocatedAmount);

        Assert.Equal(
            new Money(500m, Pln),
            invoice.OutstandingAmount);
    }

    [Fact]
    public void Allocate_should_allow_exact_outstanding_amount()
    {
        var invoice = CreateInvoice(1000m);

        invoice.Allocate(new Money(1000m, Pln));

        Assert.Equal(
            Money.Zero(Pln),
            invoice.OutstandingAmount);
    }

    [Fact]
    public void Allocate_should_reject_amount_greater_than_outstanding()
    {
        var invoice = CreateInvoice(1000m);

        Result result = invoice.Allocate(
            new Money(1001m, Pln));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code,
            result.Error?.Code);

        Assert.Equal(
            new Money(1000m, Pln),
            invoice.OutstandingAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Allocate_should_reject_non_positive_amount(
        decimal amount)
    {
        var invoice = CreateInvoice(1000m);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => invoice.Allocate(
                new Money(amount, Pln)));
    }

    [Fact]
    public void Allocate_should_reject_different_currency()
    {
        var invoice = CreateInvoice(1000m);

        Result result = invoice.Allocate(
            new Money(100m, Eur));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.CurrencyMismatch.Code,
            result.Error?.Code);
    }

    [Fact]
    public void ReverseAllocation_should_restore_outstanding_amount()
    {
        var invoice = CreateInvoice(1000m);

        invoice.Allocate(new Money(600m, Pln));

        Result result = invoice.ReverseAllocation(
            new Money(200m, Pln));

        Assert.True(result.IsSuccess);

        Assert.Equal(
            new Money(400m, Pln),
            invoice.AllocatedAmount);

        Assert.Equal(
            new Money(600m, Pln),
            invoice.OutstandingAmount);
    }

    [Fact]
    public void ReverseAllocation_should_reject_amount_greater_than_allocated()
    {
        var invoice = CreateInvoice(1000m);

        invoice.Allocate(new Money(300m, Pln));

        Result result = invoice.ReverseAllocation(
            new Money(301m, Pln));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.InvoiceReversalExceedsAllocated.Code,
            result.Error?.Code);

        Assert.Equal(
            new Money(300m, Pln),
            invoice.AllocatedAmount);
    }

    [Fact]
    public void ReverseAllocation_should_reject_different_currency()
    {
        var invoice = CreateInvoice(1000m);

        invoice.Allocate(new Money(300m, Pln));

        Result result = invoice.ReverseAllocation(
            new Money(100m, Eur));

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationErrors.CurrencyMismatch.Code,
            result.Error?.Code);

        Assert.Equal(
            new Money(300m, Pln),
            invoice.AllocatedAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Constructor_should_reject_non_positive_total_amount(
        decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Invoice(
                InvoiceId.New(),
                TestOrganizationId,
                "INV-001",
                new Money(amount, Pln)));
    }

    private static Invoice CreateInvoice(decimal amount)
    {
        return new Invoice(
            InvoiceId.New(),
            TestOrganizationId,
            "INV-001",
            new Money(amount, Pln));
    }
}