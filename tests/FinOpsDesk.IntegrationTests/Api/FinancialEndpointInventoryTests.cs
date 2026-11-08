using System.Reflection;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Authorization;
using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Access.Domain;
using FinOpsDesk.Modules.Reconciliation.Presentation;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEndpointInventoryTests : IDisposable
{
    private static readonly Dictionary<string, FinancialWriteOperation> ExpectedWriteOperations = new(StringComparer.Ordinal)
    {
        ["POST /api/imports/invoices"] = FinancialWriteOperation.ImportInvoices,
        ["POST /api/imports/bank-transactions"] = FinancialWriteOperation.ImportBankTransactions,
        ["POST /api/matches/confirm"] = FinancialWriteOperation.ConfirmExactMatch,
        ["POST /api/matches/batch/execute"] = FinancialWriteOperation.ExecuteBatchExactMatches,
        ["POST /api/allocations"] = FinancialWriteOperation.CreateAllocation,
        ["POST /api/allocations/{allocationId}/reverse"] = FinancialWriteOperation.ReverseAllocation,
        ["POST /api/review-cases"] = FinancialWriteOperation.OpenReviewCase,
        ["POST /api/review-cases/{id}/resolve"] = FinancialWriteOperation.ResolveReviewCase,
        ["POST /api/review-cases/{id}/dismiss"] = FinancialWriteOperation.DismissReviewCase,
        ["POST /api/review-cases/{id}/comments"] = FinancialWriteOperation.CommentReviewCase,
        ["POST /api/review-cases/{id}/assignment"] = FinancialWriteOperation.ClaimReviewCase,
        ["POST /api/review-cases/{id}/assignment/release"] = FinancialWriteOperation.ReleaseReviewCase,
        ["POST /api/allocations/{allocationId}/disputes"] = FinancialWriteOperation.RegisterAllocationDispute,
        ["POST /api/allocation-disputes/{disputeId}/resolution"] = FinancialWriteOperation.ResolveAllocationDispute,
        ["POST /api/allocation-disputes/{disputeId}/correction"] = FinancialWriteOperation.CorrectAllocationDispute,
    };

    private readonly FinOpsDeskApiFactory _factory;

    public FinancialEndpointInventoryTests(SqlServerFixture fixture)
    {
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public void Registered_reconciliation_routes_have_one_explicit_classification()
    {
        Dictionary<string, string?> expected = new(StringComparer.Ordinal)
        {
            ["GET /api/invoices"] = AccessPermission.InvoicesRead.Value,
            ["GET /api/bank-transactions"] = AccessPermission.BankTransactionsRead.Value,
            ["GET /api/matches"] = AccessPermission.ReconciliationRead.Value,
            ["GET /api/matches/smart"] = AccessPermission.ReconciliationRead.Value,
            ["GET /api/matches/batch/preview"] = AccessPermission.ReconciliationRead.Value,
            ["GET /api/allocations"] = AccessPermission.AllocationsRead.Value,
            ["GET /api/allocations/context"] = AccessPermission.AllocationsRead.Value,
            ["GET /api/allocations/{allocationId}/reversal-context"] = AccessPermission.AllocationsRead.Value,
            ["GET /api/allocations/{allocationId}/audit"] = AccessPermission.AuditRead.Value,
            ["GET /api/review-cases/{id}/audit"] = AccessPermission.AuditRead.Value,
            ["GET /api/review-cases/{id}/comments"] = AccessPermission.AuditRead.Value,
            ["GET /api/review-cases"] = AccessPermission.ReviewCasesRead.Value,
            ["GET /api/review-cases/summary"] = AccessPermission.ReviewCasesRead.Value,
            ["GET /api/review-cases/{id}"] = AccessPermission.ReviewCasesRead.Value,
            ["POST /api/imports/invoices"] = AccessPermission.ImportsCreate.Value,
            ["POST /api/imports/bank-transactions"] = AccessPermission.ImportsCreate.Value,
            ["POST /api/matches/confirm"] = AccessPermission.ReconciliationConfirm.Value,
            ["POST /api/matches/batch/execute"] = AccessPermission.ReconciliationConfirm.Value,
            ["POST /api/allocations"] = AccessPermission.AllocationsCreate.Value,
            ["POST /api/allocations/{allocationId}/reverse"] = AccessPermission.AllocationsReverse.Value,
            ["POST /api/allocations/{allocationId}/disputes"] = AccessPermission.AllocationDisputesRegister.Value,
            ["POST /api/review-cases"] = AccessPermission.ReviewCasesOpen.Value,
            ["POST /api/review-cases/{id}/resolve"] = AccessPermission.ReviewCasesResolve.Value,
            ["POST /api/review-cases/{id}/dismiss"] = AccessPermission.ReviewCasesResolve.Value,
            ["POST /api/review-cases/{id}/comments"] = AccessPermission.ReviewCasesComment.Value,
            ["POST /api/review-cases/{id}/assignment"] = AccessPermission.ReviewCasesAssign.Value,
            ["POST /api/review-cases/{id}/assignment/release"] = AccessPermission.ReviewCasesAssign.Value,
            ["GET /api/review-cases/{id}/assignment"] = AccessPermission.ReviewCasesAssignmentRead.Value,
            ["GET /api/allocation-disputes"] = AccessPermission.AllocationDisputesRead.Value,
            ["GET /api/allocation-disputes/{disputeId}"] = AccessPermission.AllocationDisputesRead.Value,
            ["GET /api/allocation-disputes/{disputeId}/resolution"] = AccessPermission.AllocationDisputesResolutionRead.Value,
            ["POST /api/allocation-disputes/{disputeId}/resolution"] = AccessPermission.AllocationDisputesResolve.Value,
            ["GET /api/allocation-disputes/{disputeId}/correction"] = AccessPermission.AllocationDisputesResolutionRead.Value,
            ["POST /api/allocation-disputes/{disputeId}/correction"] = AccessPermission.AllocationsReverse.Value,
        };

        Assert.Equal(
            AccessPermission.ReconciliationConfirm.Value,
            expected["POST /api/matches/confirm"]);
        Assert.Equal(
            AccessPermission.ReconciliationConfirm.Value,
            expected["POST /api/matches/batch/execute"]);
        Assert.NotEqual(
            ExpectedWriteOperations["POST /api/matches/confirm"],
            ExpectedWriteOperations["POST /api/matches/batch/execute"]);

        EndpointDataSource dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        List<RouteEndpoint> reconciliation = [];
        int reconciliationHandlers = 0;
        foreach (Endpoint endpoint in dataSource.Endpoints)
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            MethodInfo? method = route.Metadata.GetMetadata<MethodInfo>();
            bool fromModule = method?.DeclaringType?.Assembly == typeof(ReconciliationEndpoints).Assembly;
            bool insideBoundary = route.Metadata.GetMetadata<ReconciliationRouteBoundary>() is not null;
            if (fromModule)
            {
                reconciliationHandlers++;
                Assert.True(insideBoundary, route.RoutePattern.RawText);
            }

            if (insideBoundary)
            {
                reconciliation.Add(route);
            }
        }

        Assert.Equal(expected.Count, reconciliationHandlers);
        Dictionary<string, string?> actual = [];
        foreach (RouteEndpoint route in reconciliation)
        {
            Assert.Null(route.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Empty(route.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            IAuthorizeData authorize = Assert.Single(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(FinOpsDeskAuthorizationPolicies.TenantMember, authorize.Policy);
            Assert.True(string.IsNullOrEmpty(authorize.Roles));
            Assert.True(string.IsNullOrEmpty(authorize.AuthenticationSchemes));

            FinancialEndpointClassification classification = Assert.Single(
                route.Metadata.GetOrderedMetadata<FinancialEndpointClassification>());
            HttpMethodMetadata methods = Assert.IsType<HttpMethodMetadata>(
                Assert.Single(route.Metadata.GetOrderedMetadata<IHttpMethodMetadata>()));
            string httpMethod = Assert.Single(methods.HttpMethods);
            string pattern = Normalize(route.RoutePattern.RawText);
            string key = httpMethod + " " + pattern;
            bool added = actual.TryAdd(key, classification.Permission);
            Assert.True(added, key);

            if (classification.Kind == FinancialEndpointClassification.OperationKind.Read)
            {
                Assert.Equal("GET", httpMethod);
                Assert.Equal(expected[key], classification.Permission);
                Assert.Null(classification.Operation);
                Assert.False(ProductionFinancialWriteActivation.IsActivated(classification));
            }
            else
            {
                Assert.Equal(FinancialEndpointClassification.OperationKind.Write, classification.Kind);
                Assert.Equal("POST", httpMethod);
                Assert.Equal(expected[key], classification.Permission);
                Assert.False(string.IsNullOrWhiteSpace(classification.Permission));
                Assert.Equal(ExpectedWriteOperations[key], classification.Operation);
                bool activated = classification.Operation is FinancialWriteOperation.ConfirmExactMatch
                    or FinancialWriteOperation.CreateAllocation
                    or FinancialWriteOperation.ReverseAllocation;
                Assert.Equal(activated, ProductionFinancialWriteActivation.IsActivated(classification));
            }
        }

        Assert.Equal(expected.Keys.OrderBy(key => key, StringComparer.Ordinal), actual.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.DoesNotContain(
            dataSource.Endpoints.OfType<RouteEndpoint>(),
            route => Normalize(route.RoutePattern.RawText).StartsWith("/api/test/", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private static string Normalize(string? rawText)
    {
        string text = rawText ?? string.Empty;
        return text.StartsWith('/') ? text : "/" + text;
    }
}