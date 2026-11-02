using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace FinOpsDesk.Api.Authorization;

internal sealed class FinOpsDeskAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public const string UnavailableItemKey = "FinOpsDesk.AuthorizationUnavailable";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (context.Items.ContainsKey(UnavailableItemKey))
        {
            context.Response.Headers.CacheControl = "no-store";
            await Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Financial authorization could not be completed.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "auth.authorization.unavailable",
                }).ExecuteAsync(context);
            return;
        }

        if (!authorizeResult.Succeeded)
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}