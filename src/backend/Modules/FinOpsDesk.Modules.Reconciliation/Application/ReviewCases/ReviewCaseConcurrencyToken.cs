namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public static class ReviewCaseConcurrencyToken
{
    public const int RowVersionLength = 8;

    public static string Encode(byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(rowVersion);

        return Convert.ToBase64String(rowVersion);
    }

    public static bool TryDecode(string? token, out byte[] rowVersion)
    {
        rowVersion = [];
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(token);
            if (decoded.Length != RowVersionLength)
            {
                return false;
            }

            rowVersion = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}