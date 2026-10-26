using System.Globalization;
using System.Text.Json;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Allocations;

/// <summary>
/// Reads a JSON number token without accepting a value that
/// <see cref="JsonElement.GetDecimal"/> had to round.
/// </summary>
internal static class JsonDecimalLiteral
{
    private const string DecimalFormat = "0.############################";

    private const int DecimalPrecision = 29;

    private const int DecimalScale = 28;

    public static bool TryReadExact(JsonElement element, out decimal value)
    {
        value = 0m;

        if (element.ValueKind != JsonValueKind.Number
            || !TryCanonicalize(element.GetRawText(), out string? canonical))
        {
            return false;
        }

        decimal parsed;
        try
        {
            parsed = element.GetDecimal();
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        if (!string.Equals(canonical, Canonicalize(parsed), StringComparison.Ordinal))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string Canonicalize(decimal value)
    {
        if (value == 0m)
        {
            return "0";
        }

        return value.ToString(DecimalFormat, CultureInfo.InvariantCulture);
    }

    private static bool TryCanonicalize(string raw, out string? canonical)
    {
        canonical = null;

        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        int index = 0;
        bool negative = false;
        if (raw[index] == '-')
        {
            negative = true;
            index++;
        }

        if (index >= raw.Length || !char.IsAsciiDigit(raw[index]))
        {
            return false;
        }

        int integerStart = index;
        if (raw[index] == '0')
        {
            index++;
            if (index < raw.Length && char.IsAsciiDigit(raw[index]))
            {
                return false;
            }
        }
        else
        {
            while (index < raw.Length && char.IsAsciiDigit(raw[index]))
            {
                index++;
            }
        }

        string integerDigits = raw[integerStart..index];
        string fractionDigits = string.Empty;
        if (index < raw.Length && raw[index] == '.')
        {
            index++;
            int fractionStart = index;
            if (index >= raw.Length || !char.IsAsciiDigit(raw[index]))
            {
                return false;
            }

            while (index < raw.Length && char.IsAsciiDigit(raw[index]))
            {
                index++;
            }

            fractionDigits = raw[fractionStart..index];
        }

        int explicitExponent = 0;
        if (index < raw.Length && raw[index] is 'e' or 'E')
        {
            index++;
            int sign = 1;
            if (index < raw.Length && raw[index] is '+' or '-')
            {
                if (raw[index] == '-')
                {
                    sign = -1;
                }

                index++;
            }

            if (index >= raw.Length || !char.IsAsciiDigit(raw[index]))
            {
                return false;
            }

            int exponentStart = index;
            while (index < raw.Length && char.IsAsciiDigit(raw[index]))
            {
                index++;
            }

            if (!int.TryParse(
                    raw.AsSpan(exponentStart, index - exponentStart),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int magnitude))
            {
                return false;
            }

            explicitExponent = sign * magnitude;
        }

        if (index != raw.Length)
        {
            return false;
        }

        string digits = integerDigits + fractionDigits;
        int exponent;
        try
        {
            exponent = checked(explicitExponent - fractionDigits.Length);
            Normalize(ref digits, ref exponent);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (digits is "0")
        {
            canonical = "0";
            return true;
        }

        if (digits.Length > DecimalPrecision || exponent < -DecimalScale)
        {
            return false;
        }

        string unsigned;
        if (exponent >= 0)
        {
            if (digits.Length > DecimalPrecision - exponent)
            {
                return false;
            }

            unsigned = digits + new string('0', exponent);
        }
        else
        {
            int scale = -exponent;
            if (scale < digits.Length)
            {
                int split = digits.Length - scale;
                unsigned = string.Concat(digits.AsSpan(0, split), ".", digits.AsSpan(split));
            }
            else
            {
                unsigned = "0." + new string('0', scale - digits.Length) + digits;
            }
        }

        canonical = negative ? "-" + unsigned : unsigned;
        return true;
    }

    private static void Normalize(ref string digits, ref int exponent)
    {
        int end = digits.Length;
        while (end > 1 && digits[end - 1] == '0')
        {
            end--;
        }

        int start = 0;
        while (start < end - 1 && digits[start] == '0')
        {
            start++;
        }

        exponent = checked(exponent + (digits.Length - end));
        digits = digits[start..end];
        if (digits is "0")
        {
            exponent = 0;
        }
    }
}