using FinOpsDesk.Modules.Reconciliation.Application.Imports;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Imports;

public sealed class ImportedAmountParserTests
{
    [Theory]
    [InlineData("0.0001")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2300")]
    [InlineData("00.50")]
    [InlineData("0000000000000001.0000")]
    [InlineData("999999999999999.9999")]
    [InlineData("0999999999999999.9999")]
    [InlineData(" 10.00 ")]
    public void Accepts_values_that_fit_decimal_19_4(string raw)
    {
        Assert.True(ImportedAmountParser.TryParse(raw, out decimal amount));
        Assert.True(amount > 0m);
        Assert.True(amount <= ImportedAmountParser.MaxAmount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0000")]
    [InlineData("-1")]
    [InlineData("+1.00")]
    [InlineData("1e2")]
    [InlineData("1E2")]
    [InlineData("1,000.00")]
    [InlineData("1.00000")]
    [InlineData("1.23450")]
    [InlineData("1000000000000000")]
    [InlineData("1000000000000000.0000")]
    [InlineData("999999999999999.99999")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("PLN")]
    public void Rejects_values_that_do_not_fit_exactly(string raw)
    {
        Assert.False(ImportedAmountParser.TryParse(raw, out decimal amount));
        Assert.Equal(0m, amount);
    }

    [Fact]
    public void Maximum_positive_sql_value_is_accepted_exactly()
    {
        Assert.True(ImportedAmountParser.TryParse(
            "999999999999999.9999",
            out decimal amount));

        Assert.Equal(999999999999999.9999m, amount);
    }
}