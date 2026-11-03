using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Reconciliation.Presentation;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace FinOpsDesk.Api.Authorization;

// ProductionCookie reads stay fail-closed until FinancialReadAuthorization is
// Enabled. Writes also require FinancialWriteAuthorization. Neither flag grants
// access by itself. DevelopmentJwt accepts a consistent classification and does
// not read Access roles. The commit guard repeats the write decision inside the
// financial transaction.
internal sealed class FinancialApiAccessHandler
    : AuthorizationHandler<FinancialApiAccessRequirement>
{
    private readonly AuthenticationCompositionState _state;

    public FinancialApiAccessHandler(AuthenticationCompositionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _state = state;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FinancialApiAccessRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identity?.IsAuthenticated != true
            || context.Resource is not HttpContext httpContext)
        {
            return;
        }

        if (!_state.UsesProductionCookie)
        {
            if (IsConsistentKnownClassification(httpContext, out _))
            {
                context.Succeed(requirement);
            }

            return;
        }

        if (_state.ProductionFinancialAccessFailClosed
            || !IsConsistentKnownClassification(httpContext, out FinancialEndpointClassification? classification)
            || classification is null)
        {
            return;
        }

        if (classification.Kind == FinancialEndpointClassification.OperationKind.Read)
        {
            await AuthorizeCookiePermissionAsync(
                context,
                requirement,
                httpContext,
                classification,
                FinancialReadPermissionCatalog.TryGet);
            return;
        }

        if (classification.Kind == FinancialEndpointClassification.OperationKind.Write
            && _state.ProductionFinancialWritesEnabled)
        {
            await AuthorizeCookiePermissionAsync(
                context,
                requirement,
                httpContext,
                classification,
                FinancialWritePermissionCatalog.TryGet);
        }
    }

    private static async Task AuthorizeCookiePermissionAsync(
        AuthorizationHandlerContext context,
        FinancialApiAccessRequirement requirement,
        HttpContext httpContext,
        FinancialEndpointClassification classification,
        PermissionLookup tryGet)
    {
        if (!tryGet(classification.Permission, out AccessPermission required))
        {
            return;
        }

        AccessSessionGate sessions = httpContext.RequestServices.GetRequiredService<AccessSessionGate>();
        AccessSessionSnapshot? snapshot = sessions.Current;
        if (snapshot is null || snapshot.Kind == AccessSessionKind.Unavailable)
        {
            MarkUnavailable(httpContext);
            return;
        }

        if (snapshot.Kind != AccessSessionKind.Active || snapshot.ActiveOrganization is null)
        {
            return;
        }

        AccessPermissionResolution resolution = await sessions.ResolvePermissionsAsync(httpContext.RequestAborted);
        if (resolution.Kind == AccessPermissionResolutionKind.Unavailable)
        {
            MarkUnavailable(httpContext);
            return;
        }

        if (resolution.Kind == AccessPermissionResolutionKind.Resolved
            && resolution.Permissions.Contains(required))
        {
            context.Succeed(requirement);
        }
    }

    private delegate bool PermissionLookup(string? permission, out AccessPermission resolved);

    private static bool IsConsistentKnownClassification(
        HttpContext httpContext,
        out FinancialEndpointClassification? classification)
    {
        classification = null;
        Endpoint? endpoint = httpContext.GetEndpoint();
        if (endpoint is null)
        {
            return false;
        }

        IReadOnlyList<FinancialEndpointClassification> classifications = endpoint.Metadata
            .GetOrderedMetadata<FinancialEndpointClassification>();
        if (classifications.Count != 1)
        {
            return false;
        }

        FinancialEndpointClassification candidate = classifications[0];
        if (!MethodMatches(httpContext.Request.Method, candidate.Kind))
        {
            return false;
        }

        if (candidate.Kind == FinancialEndpointClassification.OperationKind.Read
            && !FinancialReadPermissionCatalog.TryGet(candidate.Permission, out _))
        {
            return false;
        }

        if (candidate.Kind == FinancialEndpointClassification.OperationKind.Write
            && !FinancialWritePermissionCatalog.TryGet(candidate.Permission, out _))
        {
            return false;
        }

        classification = candidate;
        return true;
    }

    private static bool MethodMatches(
        string method,
        FinancialEndpointClassification.OperationKind kind)
    {
        bool safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        return kind switch
        {
            FinancialEndpointClassification.OperationKind.Read => safe,
            FinancialEndpointClassification.OperationKind.Write => HttpMethods.IsPost(method)
                || HttpMethods.IsPut(method)
                || HttpMethods.IsPatch(method)
                || HttpMethods.IsDelete(method),
            _ => false,
        };
    }

    private static void MarkUnavailable(HttpContext httpContext)
    {
        httpContext.Items[FinOpsDeskAuthorizationResultHandler.UnavailableItemKey] = true;
    }
}