using System.Security.Claims;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Security;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinOpsDesk.IntegrationTests.Api;

public sealed class HttpContextCurrentOrganizationContextTests
{
    [Fact]
    public void OrganizationId_should_come_from_the_authenticated_claim()
    {
        Guid organizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        HttpContextCurrentOrganizationContext context = CreateContext(
            organizationId.ToString(),
            queryOrganizationId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            headerOrganizationId: Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));

        OrganizationId current = context.OrganizationId;

        Assert.Equal(organizationId, current.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void OrganizationId_should_fail_when_the_claim_is_not_a_usable_guid(
        string? claimValue)
    {
        HttpContextCurrentOrganizationContext context = CreateContext(claimValue);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => context.OrganizationId);

        Assert.Contains("organization claim", exception.Message, StringComparison.Ordinal);
    }

    private static HttpContextCurrentOrganizationContext CreateContext(
        string? claimValue,
        Guid? queryOrganizationId = null,
        Guid? headerOrganizationId = null)
    {
        List<Claim> claims = [];
        if (claimValue is not null)
        {
            claims.Add(new Claim("org_id", claimValue));
        }

        DefaultHttpContext httpContext = new()
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(claims, authenticationType: "Bearer")),
        };

        if (queryOrganizationId is not null)
        {
            httpContext.Request.QueryString = new QueryString(
                $"?organizationId={queryOrganizationId.Value}");
        }

        if (headerOrganizationId is not null)
        {
            httpContext.Request.Headers["X-Organization-Id"] = headerOrganizationId.Value.ToString();
        }

        return new HttpContextCurrentOrganizationContext(
            new HttpContextAccessor { HttpContext = httpContext },
            Options.Create(new JwtBearerAuthenticationSettings
            {
                OrganizationClaimType = "org_id",
            }),
            new AuthenticationCompositionState
            {
                UsesProductionCookie = false,
                ProductionFinancialAccessFailClosed = false,
                ProductionFinancialWritesEnabled = false,
            },
            new ServiceCollection().BuildServiceProvider());
    }
}