using FinOpsDesk.Modules.Reconciliation.Application;

using Microsoft.AspNetCore.Diagnostics;

namespace FinOpsDesk.Api.Authorization;

internal sealed class FinancialCommitAuthorizationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not FinancialCommitAuthorizationException authorization)
        {
            return false;
        }

        int status = authorization.Kind switch
        {
            FinancialCommitAuthorizationKind.Unauthenticated => StatusCodes.Status401Unauthorized,
            FinancialCommitAuthorizationKind.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        httpContext.Response.Headers.CacheControl = "no-store";
        await Results.Problem(
            statusCode: status,
            title: "Financial authorization could not be completed.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = authorization.Code,
            }).ExecuteAsync(httpContext);
        return true;
    }
}