using System.Globalization;
using System.Text;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public static class SmartReferenceNormalizer
{
    public const int MaxTrimmedLength = 256;

    public const int MaxTokenCount = 16;

    private const int LongDigitRunLength = 6;

    private const int SpecificNumberLength = 4;

    private const int DocumentPrefixLength = 2;

    private const int MinYear = 1900;

    private const int MaxYear = 2099;

    public static SmartReferenceAnalysis Analyze(string? value)
    {
        // Blank input is missing under exact-1 as well. Detect it before uppercasing
        // so a long whitespace string is not copied.
        if (string.IsNullOrWhiteSpace(value))
        {
            return SmartReferenceAnalysis.Missing(exactNormalized: null);
        }

        string trimmed = value.Trim();

        // Past this limit the reference cannot produce a smart candidate.
        // Skip exact-1 normalization so the oversized text is not uppercased.
        if (trimmed.Length > MaxTrimmedLength)
        {
            return SmartReferenceAnalysis.Oversized(exactNormalized: null);
        }

        string? exactNormalized = PaymentReferenceNormalizer.Normalize(value);

        string composed = trimmed.Normalize(NormalizationForm.FormC);
        if (composed.Length > MaxTrimmedLength)
        {
            return SmartReferenceAnalysis.Oversized(exactNormalized);
        }

        string upper = composed.ToUpperInvariant();
        if (upper.Length > MaxTrimmedLength)
        {
            return SmartReferenceAnalysis.Oversized(exactNormalized);
        }

        List<string> tokens = new();
        int tokenStart = -1;

        for (int index = 0; index <= upper.Length; index++)
        {
            bool separator = index == upper.Length || IsSeparator(upper[index]);

            if (!separator)
            {
                if (tokenStart < 0)
                {
                    tokenStart = index;
                }

                continue;
            }

            if (tokenStart < 0)
            {
                continue;
            }

            tokens.Add(upper[tokenStart..index]);
            tokenStart = -1;

            if (tokens.Count > MaxTokenCount)
            {
                return SmartReferenceAnalysis.Oversized(exactNormalized);
            }
        }

        List<string> longDigitRuns = new();
        List<string> yearTokens = new();
        bool documentPrefix = false;
        bool specificNumber = false;

        foreach (string token in tokens)
        {
            if (IsDocumentPrefix(token))
            {
                documentPrefix = true;
            }

            if (IsYear(token))
            {
                yearTokens.Add(token);
            }
            else if (IsSpecificNumber(token))
            {
                specificNumber = true;
            }

            CollectLongDigitRuns(token, longDigitRuns);
        }

        yearTokens.Sort(StringComparer.Ordinal);
        string? tokenSetKey = documentPrefix && specificNumber && tokens.Count >= 2
            ? BuildTokenSetKey(tokens)
            : null;
        string? compactForm = tokens.Count == 0 ? null : string.Concat(tokens);

        return SmartReferenceAnalysis.Create(
            exactNormalized,
            tokens,
            longDigitRuns,
            yearTokens,
            tokenSetKey,
            compactForm);
    }

    private static string BuildTokenSetKey(List<string> tokens)
    {
        string[] sorted = new string[tokens.Count];
        tokens.CopyTo(sorted);
        Array.Sort(sorted, StringComparer.Ordinal);
        return string.Join('\u001f', sorted);
    }

    private static void CollectLongDigitRuns(string token, List<string> runs)
    {
        int start = -1;

        for (int index = 0; index <= token.Length; index++)
        {
            bool digit = index < token.Length && IsAsciiDigit(token[index]);

            if (digit)
            {
                if (start < 0)
                {
                    start = index;
                }

                continue;
            }

            if (start < 0)
            {
                continue;
            }

            int length = index - start;
            if (length >= LongDigitRunLength)
            {
                runs.Add(token.Substring(start, length));
            }

            start = -1;
        }
    }

    private static bool IsDocumentPrefix(string token)
    {
        if (token.Length < DocumentPrefixLength)
        {
            return false;
        }

        foreach (char character in token)
        {
            if (!char.IsLetter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSpecificNumber(string token)
    {
        return token.Length >= SpecificNumberLength && IsAllAsciiDigits(token) && !IsYear(token);
    }

    private static bool IsYear(string token)
    {
        if (token.Length != SpecificNumberLength || !IsAllAsciiDigits(token))
        {
            return false;
        }

        int year = int.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture);
        return year >= MinYear && year <= MaxYear;
    }

    private static bool IsAllAsciiDigits(string token)
    {
        foreach (char character in token)
        {
            if (!IsAsciiDigit(character))
            {
                return false;
            }
        }

        return token.Length > 0;
    }

    private static bool IsAsciiDigit(char character)
    {
        return character is >= '0' and <= '9';
    }

    private static bool IsSeparator(char character)
    {
        return char.IsWhiteSpace(character)
            || character is '/' or '\\' or '-' or '_' or '.' or ',' or ':' or ';' or '#' or '(' or ')';
    }
}