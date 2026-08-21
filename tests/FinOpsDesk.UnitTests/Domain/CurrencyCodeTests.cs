using FinOpsDesk.Modules.Reconciliation.Domain;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain;

public sealed class CurrencyCodeTests
{
    [Fact]
    public void Constructor_should_normalize_currency_code()
    {
        var currency = new CurrencyCode(" pln ");

        Assert.Equal("PLN", currency.Value);
    }

    [Fact]
    public void Same_currency_codes_should_be_equal()
    {
        var first = new CurrencyCode("pln");
        var second = new CurrencyCode("PLN");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("PL")]
    [InlineData("PLNN")]
    [InlineData("P1N")]
    public void Constructor_should_reject_invalid_currency_code(string value)
    {
        Assert.Throws<ArgumentException>(
            () => new CurrencyCode(value));
    }
}