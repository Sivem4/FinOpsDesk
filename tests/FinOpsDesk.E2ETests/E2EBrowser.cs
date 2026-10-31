using System.Text;
using System.Text.Json;

using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

internal static class E2EBrowser
{
    public const string ReversalAttemptKey = "finopsdesk.reversal-attempt";

    public const string ExplicitAmountAttemptKey = "finopsdesk.explicit-amount-attempt";

    public static async Task<string?> ReadSessionAsync(IPage page, string key)
    {
        return await page.EvaluateAsync<string?>("storageKey => sessionStorage.getItem(storageKey)", key);
    }

    public static async Task<int> LocalStorageLengthAsync(IPage page)
    {
        return await page.EvaluateAsync<int>("() => window.localStorage.length");
    }

    public static async Task SetSessionAsync(IPage page, string key, string value)
    {
        await page.EvaluateAsync(
            "payload => sessionStorage.setItem(payload.key, payload.value)",
            new Dictionary<string, string>
            {
                ["key"] = key,
                ["value"] = value,
            });
    }

    public static async Task RequireStoredRequestAsync(IPage page, string key, E2ECapturedRequest request)
    {
        string? raw = await ReadSessionAsync(page, key);
        if (string.IsNullOrEmpty(raw))
        {
            throw new Xunit.Sdk.XunitException("The stored financial attempt is missing.");
        }

        string? storedKey;
        string? storedBody;
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            storedKey = document.RootElement.GetProperty("idempotencyKey").GetString();
            storedBody = document.RootElement.GetProperty("requestBody").GetString();
        }
        catch (JsonException)
        {
            throw new Xunit.Sdk.XunitException("The stored financial attempt could not be read.");
        }

        if (!string.Equals(storedKey, request.IdempotencyKey, StringComparison.Ordinal)
            || storedBody is null
            || !request.Body.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(storedBody)))
        {
            throw new Xunit.Sdk.XunitException(
                "The stored financial attempt does not match the intercepted POST.");
        }
    }

    public static void RequireNoOrganizationId(E2ECapturedRequest request, Guid organizationId)
    {
        string body = Encoding.UTF8.GetString(request.Body);
        if (body.Contains(organizationId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || body.Contains(organizationId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            throw new Xunit.Sdk.XunitException("The financial request body contains an organization id.");
        }
    }

    public static void RequireUnchanged(string? before, string? after, string label)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            throw new Xunit.Sdk.XunitException(label + " changed.");
        }
    }
}