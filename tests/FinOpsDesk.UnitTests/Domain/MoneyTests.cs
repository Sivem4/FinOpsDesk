using FinOpsDesk.Modules.Reconciliation.Domain;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain;

public sealed class MoneyTests
{
    private static readonly CurrencyCode Pln = new("PLN");
    private static readonly CurrencyCode Eur = new("EUR");

    [Fact]
    public void Same_amount_and_currency_should_be_equal()
    {
        var first = new Money(100m, Pln);
        var second = new Money(100m, Pln);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Same_amount_with_different_currency_should_not_be_equal()
    {
        var pln = new Money(100m, Pln);
        var eur = new Money(100m, Eur);

        Assert.NotEqual(pln, eur);
    }

    [Fact]
    public void Add_should_add_money_with_same_currency()
    {
        var first = new Money(100m, Pln);
        var second = new Money(50m, Pln);

        Money result = first.Add(second);

        Assert.Equal(
            new Money(150m, Pln),
            result);
    }

    [Fact]
    public void Add_should_reject_different_currency()
    {
        var pln = new Money(100m, Pln);
        var eur = new Money(50m, Eur);

        Assert.Throws<InvalidOperationException>(
            () => pln.Add(eur));
    }

    [Fact]
    public void Subtract_should_subtract_money_with_same_currency()
    {
        var first = new Money(100m, Pln);
        var second = new Money(40m, Pln);

        Money result = first.Subtract(second);

        Assert.Equal(
            new Money(60m, Pln),
            result);
    }

    [Fact]
    public void IsGreaterThan_should_return_true_when_amount_is_greater()
    {
        var greater = new Money(100m, Pln);
        var smaller = new Money(40m, Pln);

        Assert.True(greater.IsGreaterThan(smaller));
    }

    [Fact]
    public void IsGreaterThan_should_return_false_when_amounts_are_equal()
    {
        var first = new Money(100m, Pln);
        var second = new Money(100m, Pln);

        Assert.False(first.IsGreaterThan(second));
    }

    [Fact]
    public void IsGreaterThan_should_return_false_when_amount_is_smaller()
    {
        var smaller = new Money(40m, Pln);
        var greater = new Money(100m, Pln);

        Assert.False(smaller.IsGreaterThan(greater));
    }

    [Fact]
    public void IsGreaterThan_should_reject_different_currency()
    {
        var pln = new Money(100m, Pln);
        var eur = new Money(50m, Eur);

        Assert.Throws<InvalidOperationException>(
            () => pln.IsGreaterThan(eur));
    }

    [Fact]
    public void Money_should_allow_negative_amount()
    {
        var money = new Money(-100m, Pln);

        Assert.Equal(-100m, money.Amount);
    }

    [Fact]
    public void Zero_should_create_zero_money_for_currency()
    {
        Money money = Money.Zero(Pln);

        Assert.Equal(
            new Money(0m, Pln),
            money);
    }
}