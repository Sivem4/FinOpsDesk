using System.Security.Claims;

using FinOpsDesk.Api.Authentication;
using FinOpsDesk.Api.Security;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

public sealed class HttpContextCurrentActorContextTests
{
    [Fact]
    public void TryGetActorId_reads_the_authenticated_sub_claim()
    {
        HttpContextCurrentActorContext context = CreateContext(sub: "operator-a");

        bool found = context.TryGetActorId(out string actorId);

        Assert.True(found);
        Assert.Equal("operator-a", actorId);
    }

    [Fact]
    public void TryGetActorId_trims_the_sub_claim()
    {
        HttpContextCurrentActorContext context = CreateContext(sub: "  operator-a  ");

        bool found = context.TryGetActorId(out string actorId);

        Assert.True(found);
        Assert.Equal("operator-a", actorId);
    }

    [Fact]
    public void TryGetActorId_accepts_256_characters()
    {
        string subject = new('a', FinancialAuditEntry.MaxActorIdLength);
        HttpContextCurrentActorContext context = CreateContext(sub: subject);

        bool found = context.TryGetActorId(out string actorId);

        Assert.True(found);
        Assert.Equal(subject, actorId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetActorId_rejects_a_missing_or_blank_sub_claim(string? sub)
    {
        HttpContextCurrentActorContext context = CreateContext(sub);

        bool found = context.TryGetActorId(out string actorId);

        Assert.False(found);
        Assert.Equal(string.Empty, actorId);
    }

    [Fact]
    public void TryGetActorId_rejects_a_subject_longer_than_256_characters()
    {
        HttpContextCurrentActorContext context = CreateContext(
            sub: new string('a', FinancialAuditEntry.MaxActorIdLength + 1));

        bool found = context.TryGetActorId(out string actorId);

        Assert.False(found);
        Assert.Equal(string.Empty, actorId);
    }

    [Fact]
    public void TryGetActorId_rejects_an_unauthenticated_principal()
    {
        DefaultHttpContext httpContext = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "operator-a")])),
        };
        var context = new HttpContextCurrentActorContext(
            new HttpContextAccessor { HttpContext = httpContext },
            DevelopmentJwtState(),
            new ServiceCollection().BuildServiceProvider());

        bool found = context.TryGetActorId(out _);

        Assert.False(found);
    }

    [Fact]
    public void TryGetActorId_ignores_request_values_and_non_sub_claims()
    {
        DefaultHttpContext httpContext = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("actorId", "from-claim")],
                authenticationType: "Bearer")),
        };
        httpContext.Request.QueryString = new QueryString("?actorId=from-query");
        httpContext.Request.Headers["X-Actor-Id"] = "from-header";
        var context = new HttpContextCurrentActorContext(
            new HttpContextAccessor { HttpContext = httpContext },
            DevelopmentJwtState(),
            new ServiceCollection().BuildServiceProvider());

        bool found = context.TryGetActorId(out _);

        Assert.False(found);
    }

    private static HttpContextCurrentActorContext CreateContext(string? sub)
    {
        List<Claim> claims = [];
        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub));
        }

        DefaultHttpContext httpContext = new()
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(claims, authenticationType: "Bearer")),
        };
        httpContext.Request.QueryString = new QueryString("?actorId=from-query");
        httpContext.Request.Headers["X-Actor-Id"] = "from-header";

        return new HttpContextCurrentActorContext(
            new HttpContextAccessor { HttpContext = httpContext },
            DevelopmentJwtState(),
            new ServiceCollection().BuildServiceProvider());
    }

    private static AuthenticationCompositionState DevelopmentJwtState()
    {
        return new AuthenticationCompositionState
        {
            UsesProductionCookie = false,
            ProductionFinancialAccessFailClosed = false,
            ProductionFinancialWritesEnabled = false,
        };
    }
}