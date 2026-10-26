using System.Globalization;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Allocations;

public sealed class ExplicitAmountValidationTests
{
    private static readonly CurrencyCode Pln = new("PLN");

    [Theory]
    [InlineData("400.00")]
    [InlineData("400.00000")]
    [InlineData("0.0001")]
    [InlineData("0.00010")]
    [InlineData("999999999999999.9999")]
    public void Validate_should_accept_values_that_fit_decimal_19_4(string amount)
    {
        Result result = ExplicitAmountValidation.Validate(
            new Money(decimal.Parse(amount, CultureInfo.InvariantCulture), Pln));

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0.0001")]
    [InlineData("0.00001")]
    [InlineData("1.23456")]
    [InlineData("1000000000000000")]
    [InlineData("1000000000000000.0001")]
    public void Validate_should_reject_values_that_cannot_be_stored_exactly(string amount)
    {
        Result result = ExplicitAmountValidation.Validate(
            new Money(decimal.Parse(amount, CultureInfo.InvariantCulture), Pln));

        Assert.Equal(
            ReconciliationApplicationErrors.ExplicitAmountInvalid.Code,
            result.Error?.Code);
    }
}