using System.Security.Claims;

using FinOpsDesk.Api.Security;

namespace FinOpsDesk.IntegrationTests.Api;

public sealed class OrganizationClaimParserTests
{
    private const string ClaimType = "org_id";

    [Fact]
    public void TryGetOrganizationId_should_read_a_non_empty_guid_claim()
    {
        Guid organizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        ClaimsPrincipal principal = CreatePrincipal(organizationId.ToString());

        bool parsed = OrganizationClaimParser.TryGetOrganizationId(
            principal,
            ClaimType,
            out Guid parsedOrganizationId);

        Assert.True(parsed);
        Assert.Equal(organizationId, parsedOrganizationId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryGetOrganizationId_should_reject_a_missing_or_unusable_claim(
        string? claimValue)
    {
        ClaimsPrincipal principal = claimValue is null
            ? CreatePrincipal(claimValue: null)
            : CreatePrincipal(claimValue);

        bool parsed = OrganizationClaimParser.TryGetOrganizationId(
            principal,
            ClaimType,
            out Guid parsedOrganizationId);

        Assert.False(parsed);
        Assert.Equal(Guid.Empty, parsedOrganizationId);
    }

    [Fact]
    public void TryGetOrganizationId_should_reject_an_unauthenticated_principal()
    {
        Guid organizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimType, organizationId.ToString())]));

        bool parsed = OrganizationClaimParser.TryGetOrganizationId(
            principal,
            ClaimType,
            out _);

        Assert.False(parsed);
    }

    [Fact]
    public void TryGetOrganizationId_should_reject_a_null_principal()
    {
        bool parsed = OrganizationClaimParser.TryGetOrganizationId(
            principal: null,
            ClaimType,
            out _);

        Assert.False(parsed);
    }

    private static ClaimsPrincipal CreatePrincipal(string? claimValue)
    {
        List<Claim> claims = [];
        if (claimValue is not null)
        {
            claims.Add(new Claim(ClaimType, claimValue));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }
}