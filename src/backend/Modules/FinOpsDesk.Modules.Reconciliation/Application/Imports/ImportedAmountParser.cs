using System.Globalization;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public static class ImportedAmountParser
{
    public const decimal MaxAmount = 999999999999999.9999m;

    public static bool TryParse(string? raw, out decimal amount)
    {
        amount = 0m;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string text = raw.Trim();
        int separator = text.IndexOf('.');

        if (separator != text.LastIndexOf('.'))
        {
            return false;
        }

        string integerPart = separator < 0 ? text : text[..separator];
        string fraction = separator < 0 ? string.Empty : text[(separator + 1)..];

        if (integerPart.Length == 0
            || (separator >= 0 && fraction.Length == 0)
            || fraction.Length > 4
            || !IsDigits(integerPart)
            || !IsDigits(fraction))
        {
            return false;
        }

        string significant = integerPart.TrimStart('0');

        if (significant.Length == 0)
        {
            significant = "0";
        }

        if (significant.Length > 15)
        {
            return false;
        }

        decimal parsed = decimal.Parse(
            fraction.Length == 0 ? significant : significant + "." + fraction,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture);
        decimal composed = Compose(significant, fraction);

        if (parsed != composed || parsed <= 0m || parsed > MaxAmount)
        {
            return false;
        }

        amount = parsed;
        return true;
    }

    private static decimal Compose(string significantInteger, string fraction)
    {
        decimal integer = decimal.Parse(significantInteger, CultureInfo.InvariantCulture);

        if (fraction.Length == 0)
        {
            return integer;
        }

        decimal divisor = fraction.Length switch
        {
            1 => 10m,
            2 => 100m,
            3 => 1000m,
            4 => 10000m,
            _ => throw new InvalidOperationException("Fractional digit count was already validated.")
        };

        return integer + (decimal.Parse(fraction, CultureInfo.InvariantCulture) / divisor);
    }

    private static bool IsDigits(string value)
    {
        foreach (char character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}