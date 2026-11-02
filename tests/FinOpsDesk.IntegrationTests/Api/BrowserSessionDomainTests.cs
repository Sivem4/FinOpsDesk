using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.IntegrationTests.Api;

public sealed class BrowserSessionDomainTests
{
    [Fact]
    public void Open_rejects_an_empty_identifier()
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => BrowserSession.Open(Guid.Empty, Guid.NewGuid(), createdAt, createdAt.AddHours(8)));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void Open_rejects_an_expiry_that_is_not_later()
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => BrowserSession.Open(Guid.NewGuid(), Guid.NewGuid(), createdAt, createdAt));

        Assert.Equal("expiresAt", exception.ParamName);
    }

    [Fact]
    public void CreateId_returns_distinct_non_empty_identifiers()
    {
        Guid first = BrowserSession.CreateId();
        Guid second = BrowserSession.CreateId();

        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(first, second);
    }
}