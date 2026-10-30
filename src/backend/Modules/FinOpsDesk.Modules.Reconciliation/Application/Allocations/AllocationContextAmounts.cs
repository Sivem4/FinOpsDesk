using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

/// <summary>
/// Reads one stored principal and allocated pair. The checks match
/// <c>ReviewCaseQueries.TryGetRemaining</c> and also reject a value that
/// would not fit <c>decimal(19,4)</c> without rounding.
/// </summary>
internal static class AllocationContextAmounts
{
    private const int Scale = 4;

    private static readonly decimal MaximumExclusive = 1_000_000_000_000_000m;

    public static bool TryRead(
        decimal principalAmount,
        string? principalCurrency,
        decimal allocatedAmount,
        string? allocatedCurrency,
        out MoneyDto principal,
        out MoneyDto allocated,
        out MoneyDto remaining)
    {
        principal = null!;
        allocated = null!;
        remaining = null!;

        if (!HasSupportedMagnitude(principalAmount)
            || !HasSupportedMagnitude(allocatedAmount)
            || principalAmount <= 0m
            || allocatedAmount < 0m
            || !TryNormalizeCurrency(principalCurrency, out string principalCode)
            || !TryNormalizeCurrency(allocatedCurrency, out string allocatedCode)
            || !string.Equals(principalCode, allocatedCode, StringComparison.Ordinal))
        {
            return false;
        }

        Money principalMoney = new(principalAmount, new CurrencyCode(principalCode));
        Money allocatedMoney = new(allocatedAmount, new CurrencyCode(allocatedCode));
        if (allocatedMoney.IsGreaterThan(principalMoney))
        {
            return false;
        }

        Money remainingMoney = principalMoney.Subtract(allocatedMoney);
        principal = new MoneyDto(principalMoney.Amount, principalMoney.Currency.Value);
        allocated = new MoneyDto(allocatedMoney.Amount, allocatedMoney.Currency.Value);
        remaining = new MoneyDto(remainingMoney.Amount, remainingMoney.Currency.Value);
        return true;
    }

    private static bool HasSupportedMagnitude(decimal amount)
    {
        if (amount <= -MaximumExclusive || amount >= MaximumExclusive)
        {
            return false;
        }

        decimal stored = decimal.Round(amount, Scale, MidpointRounding.AwayFromZero);
        return amount == stored;
    }

    private static bool TryNormalizeCurrency(string? value, out string currency)
    {
        currency = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 3)
        {
            return false;
        }

        foreach (char character in normalized)
        {
            if (character is < 'A' or > 'Z')
            {
                return false;
            }
        }

        currency = normalized;
        return true;
    }
}